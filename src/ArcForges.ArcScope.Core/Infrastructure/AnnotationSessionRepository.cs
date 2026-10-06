// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Persistence.Sqlite;
using Google.Protobuf;

namespace ArcForges.ArcScope.Core.Infrastructure;

/// <summary>Owned annotation metadata and command receipts share one durable mutation.</summary>
internal sealed class AnnotationSessionRepository
{
    private const string Header = "ArcForges.ArcScope.AnnotationSession.v1";
    private const int MaximumPayloadBytes = 1024 * 1024;
    private const int MaximumCommands = 256;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly DurableRegistry registry;

    internal sealed record Receipt(Guid CommandId, byte[] Fingerprint, long CommittedVersion);
    internal sealed record Session(Guid Id, long Version, ScopeMetadata Metadata, IReadOnlyList<Receipt> Receipts);

    internal AnnotationSessionRepository(IStore store, Guid partitionId, UserId actor, IClock clock) =>
        registry = new(store, "arcscope.annotation.sessions.v1", partitionId, actor, clock,
            entries => entries.Select(Decode).SelectMany(session =>
                session.Metadata.Annotations.SelectMany(annotation => annotation.Origin.Kinds)
                    .Concat(session.Metadata.Findings.Where(finding => finding.Origin is not null).SelectMany(finding => finding.Origin.Kinds))));

    internal Session? Read(Guid id)
    {
        var entry = registry.Read(id);
        return entry is null ? null : Decode(entry);
    }

    internal IReadOnlyList<Session> ReadAll() => registry.ReadAll().Select(Decode).ToArray();

    internal ValueTask<bool> TryCreateAsync(Guid id, ScopeMetadata metadata, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        var snapshot = new Session(id, 1, metadata.Clone(), []);
        return registry.TryCreateAsync(new(id, snapshot.Version, Encode(snapshot)), cancellationToken);
    }

    internal ValueTask<bool> TryAppendAsync(Session current, ScopeAnnotation annotation, Guid commandId,
        ReadOnlySpan<byte> fingerprint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(annotation);
        if (commandId == Guid.Empty || fingerprint.Length != 32) throw new ArgumentException("Exact command identity and fingerprint are required.");
        if (current.Receipts.Count >= MaximumCommands) throw new InvalidOperationException("Session command receipt capacity is full; no receipt is evicted.");
        if (current.Metadata.Annotations.Count >= 128) throw new InvalidOperationException("Session annotation capacity is full.");
        var copy = current.Metadata.Clone();
        copy.Annotations.Add(annotation.Clone());
        var next = new Session(current.Id, checked(current.Version + 1), copy,
            [.. current.Receipts, new(commandId, fingerprint.ToArray(), checked(current.Version + 1))]);
        return registry.TryReplaceAsync(new(current.Id, next.Version, Encode(next)), current.Version, cancellationToken);
    }

    private static byte[] Encode(Session session)
    {
        Validate(session);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Header);
            var metadata = session.Metadata.ToByteArray();
            writer.Write(metadata.Length);
            writer.Write(metadata);
            writer.Write(session.Receipts.Count);
            foreach (var receipt in session.Receipts.OrderBy(receipt => receipt.CommandId))
            {
                writer.Write(receipt.CommandId.ToByteArray());
                writer.Write(receipt.Fingerprint);
                writer.Write(receipt.CommittedVersion);
            }
        }
        if (stream.Length > MaximumPayloadBytes) throw new InvalidOperationException("The session byte budget is full.");
        return stream.ToArray();
    }

    private static Session Decode(DurableRegistry.Entry entry)
    {
        if (entry.Payload.Length > MaximumPayloadBytes) throw new InvalidDataException("Invalid annotation session byte budget.");
        using var stream = new MemoryStream(entry.Payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (reader.ReadString() != Header) throw new InvalidDataException("Unsupported annotation session format.");
        var count = reader.ReadInt32();
        if (count <= 0 || count > MaximumPayloadBytes || count > stream.Length - stream.Position)
            throw new InvalidDataException("Invalid annotation metadata size.");
        using var metadataBytes = new MemoryStream(reader.ReadBytes(count), writable: false);
        using var metadataInput = CodedInputStream.CreateWithLimits(metadataBytes, MaximumPayloadBytes, 32);
        var metadata = ScopeMetadata.Parser.ParseFrom(metadataInput);
        var receiptCount = reader.ReadInt32();
        if (receiptCount is < 0 or > MaximumCommands) throw new InvalidDataException("Invalid annotation receipt count.");
        var receipts = new Receipt[receiptCount];
        for (var i = 0; i < receiptCount; i++)
        {
            var idBytes = reader.ReadBytes(16);
            var fingerprint = reader.ReadBytes(32);
            if (idBytes.Length != 16 || fingerprint.Length != 32) throw new EndOfStreamException();
            receipts[i] = new(new Guid(idBytes), fingerprint, reader.ReadInt64());
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing annotation session data.");
        var session = new Session(entry.Id, entry.Version, metadata, receipts);
        Validate(session);
        return session;
    }

    private static void Validate(Session session)
    {
        if (session.Id == Guid.Empty || session.Version <= 0 || session.Metadata.SessionId is null ||
            UuidBoundary.FromWire(session.Metadata.SessionId) != session.Id || session.Metadata.Annotations.Count > 128 ||
            session.Receipts.Count > MaximumCommands || session.Receipts.Select(receipt => receipt.CommandId).Distinct().Count() != session.Receipts.Count ||
            session.Receipts.Any(receipt => receipt.CommandId == Guid.Empty || receipt.Fingerprint.Length != 32 ||
                receipt.CommittedVersion <= 1 || receipt.CommittedVersion > session.Version))
            throw new InvalidDataException("Invalid owned session identity, version or receipt binding.");
        var identities = new HashSet<Guid>();
        foreach (var annotation in session.Metadata.Annotations)
        {
            var range = annotation.Range;
            if (annotation.AnnotationId is null || !identities.Add(UuidBoundary.FromWire(annotation.AnnotationId)) ||
                range is null || !range.HasFrom || !range.HasCount || ulong.MaxValue - range.From < range.Count ||
                !annotation.HasText || string.IsNullOrWhiteSpace(annotation.Text) || StrictUtf8.GetByteCount(annotation.Text) > 4096 ||
                annotation.Origin is null || annotation.Origin.Profile != "arcforges.content-origin.v1" ||
                annotation.Origin.OriginId is null || annotation.Origin.ContentUnitId is null ||
                ContentUnitId.FromWire(annotation.Origin.ContentUnitId).Value != UuidBoundary.FromWire(annotation.AnnotationId) ||
                annotation.Origin.ProducerKind is not ("human" or "model" or "deterministic" or "import") ||
                !annotation.Origin.HasOmittedParentCount || annotation.Origin.OmittedParentCount > int.MaxValue ||
                annotation.Origin.ParentOriginIds.Count > 32 || annotation.Origin.OmittedParentCount > 0 && annotation.Origin.ParentOriginIds.Count != 32 ||
                !annotation.Origin.Kinds.SequenceEqual(new[] { "aiGenerated", "aiManipulated", "nonAi", "unknown" }.Where(annotation.Origin.Kinds.Contains)) ||
                annotation.Origin.Kinds.Count == 0 ||
                annotation.Origin.PayloadSha256 != Convert.ToHexStringLower(SHA256.HashData(StrictUtf8.GetBytes(annotation.Text))))
                throw new InvalidDataException("Invalid persisted annotation identity, range, content or origin.");
            _ = ContentOriginId.FromWire(annotation.Origin.OriginId);
            var parents = annotation.Origin.ParentOriginIds.Select(id => ContentOriginId.FromWire(id).Value.ToString("N")).ToArray();
            if (!parents.SequenceEqual(parents.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)))
                throw new InvalidDataException("Annotation origin parents must be unique and canonically ordered.");
            if (annotation.Origin.CreatedAt is not null) _ = WireValues.ReadInstant(annotation.Origin.CreatedAt);
        }
    }
}
