// SPDX-License-Identifier: AGPL-3.0-only
using System.Security.Cryptography;
using System.Text;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;
using ArcForges.Persistence.Sqlite;
using ContentOrigin = ArcForges.Contracts.Foundation.V1.ContentOrigin;

namespace ArcForges.ArcScope.Core.Infrastructure;

/// <summary>A bounded, atomic registry: records and their discoverable identities commit together.</summary>
internal sealed class DurableRegistry
{
    private const int MaximumEntries = 16384;
    private const int MaximumEntryBytes = 1024 * 1024;
    private const int MaximumRegistryBytes = 16 * 1024 * 1024;
    private const string Header = "ArcForges.ArcScope.Registry.v1";
    private readonly IStore store;
    private readonly string kind;
    private readonly Guid rootId;
    private readonly Guid contentUnitId;
    private readonly UserId actor;
    private readonly IClock clock;
    private readonly Func<IReadOnlyCollection<Entry>, IEnumerable<string>>? inheritedKinds;

    internal DurableRegistry(IStore store, string kind, Guid rootId, UserId actor, IClock clock,
        Func<IReadOnlyCollection<Entry>, IEnumerable<string>>? inheritedKinds = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentNullException.ThrowIfNull(clock);
        if (rootId == Guid.Empty) throw new ArgumentException("A root identity is required.", nameof(rootId));
        _ = actor.ToWire();
        this.store = store; this.kind = kind; this.rootId = rootId; this.actor = actor; this.clock = clock;
        // The same owner partition can contain several registry kinds. Their content identities are distinct.
        contentUnitId = new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(kind + "\0" + rootId.ToString("D"))).AsSpan(0, 16));
        this.inheritedKinds = inheritedKinds;
    }

    internal sealed record Entry(Guid Id, long Version, byte[] Payload);
    private sealed record Snapshot(StoreVersion Version, ContentOrigin? Origin, SortedDictionary<Guid, Entry> Entries);

    internal Entry? Read(Guid id) => ReadSnapshot().Entries.GetValueOrDefault(id);
    internal IReadOnlyList<Entry> ReadAll() => ReadSnapshot().Entries.Values.ToArray();

    internal async ValueTask<bool> TryCreateAsync(Entry entry, CancellationToken cancellationToken)
    {
        Validate(entry);
        if (entry.Version != 1) return false;
        return await MutateAsync(entry, expectedVersion: null, cancellationToken).ConfigureAwait(false);
    }

    internal async ValueTask<bool> TryReplaceAsync(Entry entry, long expectedVersion, CancellationToken cancellationToken)
    {
        Validate(entry);
        if (expectedVersion <= 0 || expectedVersion == long.MaxValue || entry.Version != expectedVersion + 1) return false;
        return await MutateAsync(entry, expectedVersion, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<bool> MutateAsync(Entry entry, long? expectedVersion, CancellationToken cancellationToken)
    {
        // Cross-process concurrency is decided by SQLite's root CAS, never this instance's memory.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var before = ReadSnapshot();
            var current = before.Entries.GetValueOrDefault(entry.Id);
            if (expectedVersion is null ? current is not null : current?.Version != expectedVersion) return false;
            if (current is null && before.Entries.Count >= MaximumEntries)
                throw new InvalidOperationException("The durable registry is full; existing records remain intact.");
            before.Entries[entry.Id] = entry with { Payload = entry.Payload.ToArray() };
            var payload = Encode(before.Entries);
            var next = StoreVersion.Native(new NativeRevision(before.Version.Kind == StoreVersionKind.NewRoot
                ? 1 : checked(before.Version.NativeRevision!.Value.Value + 1)));
            var now = clock.GetCurrentInstant();
            var origin = new ContentOrigin
            {
                Profile = "arcforges.content-origin.v1",
                OriginId = new ContentOriginId(Guid.NewGuid()).ToWire(),
                ContentUnitId = new ContentUnitId(contentUnitId).ToWire(),
                PayloadSha256 = Convert.ToHexStringLower(SHA256.HashData(payload)),
                ProducerKind = "deterministic",
                OmittedParentCount = 0,
            };
            string[] kindOrder = ["aiGenerated", "aiManipulated", "nonAi", "unknown"];
            var kinds = new HashSet<string>(before.Origin?.Kinds ?? [], StringComparer.Ordinal) { "nonAi" };
            if (inheritedKinds is not null) kinds.UnionWith(inheritedKinds(before.Entries.Values));
            if (kinds.Except(kindOrder, StringComparer.Ordinal).Any()) throw new InvalidDataException("Unknown inherited content-origin kind.");
            origin.Kinds.Add(kindOrder.Where(kinds.Contains));
            if (before.Origin is not null) origin.ParentOriginIds.Add(before.Origin.OriginId.Clone());
            var command = new WriteCommand(new CommandId(Guid.NewGuid()), kind, rootId, before.Version,
                new StoredContent(next, payload, origin), kind + ".commit", actor, Guid.NewGuid(), now);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                _ = store.Write(command);
                return true; // Cancellation after commit must not misreport a durable mutation.
            }
            catch (Exception)
            {
                var observed = ReadSnapshot();
                if (observed.Version == before.Version) throw; // Safe start, policy or lifecycle refusal, not CAS contention.
                // Receipt lookup precedes CAS inside IStore. Even if another writer advanced the root after this command,
                // replaying its exact immutable receipt reconciles post-commit notification failure without another effect.
                try
                {
                    var receipt = store.Write(command);
                    if (!receipt.Replayed) throw new InvalidDataException("Reconciliation unexpectedly repeated a mutation.");
                    return true;
                }
                catch (InvalidOperationException)
                {
                    if (observed.Version == next && observed.Entries.TryGetValue(entry.Id, out var candidate) &&
                        candidate.Version == entry.Version && candidate.Payload.AsSpan().SequenceEqual(entry.Payload))
                        return false;
                }
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(4 << attempt, 128)), cancellationToken).ConfigureAwait(false);
        }
        throw new IOException("The durable registry remained contended after bounded retries.");
    }

    private Snapshot ReadSnapshot()
    {
        var content = store.Read(kind, rootId);
        if (content is null) return new(StoreVersion.NewRoot, null, []);
        var origin = content.Origin;
        if (content.Version.Kind != StoreVersionKind.Native || content.Payload.Length > MaximumRegistryBytes ||
            origin.Profile != "arcforges.content-origin.v1" || origin.ContentUnitId is null ||
            ContentUnitId.FromWire(origin.ContentUnitId).Value != contentUnitId ||
            origin.PayloadSha256 != Convert.ToHexStringLower(SHA256.HashData(content.Payload.Span)))
            throw new InvalidDataException("The durable registry identity, version or payload origin is invalid.");
        using var stream = new MemoryStream(content.Payload.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (reader.ReadString() != Header) throw new InvalidDataException("Unsupported durable registry format; writes are refused.");
        var count = reader.ReadInt32();
        if (count is < 0 or > MaximumEntries) throw new InvalidDataException("Invalid durable registry count.");
        var entries = new SortedDictionary<Guid, Entry>();
        for (var index = 0; index < count; index++)
        {
            var idBytes = reader.ReadBytes(16);
            if (idBytes.Length != 16) throw new EndOfStreamException();
            var id = new Guid(idBytes);
            var version = reader.ReadInt64();
            var length = reader.ReadInt32();
            if (length is <= 0 or > MaximumEntryBytes || length > stream.Length - stream.Position)
                throw new InvalidDataException("Invalid durable record size.");
            var entry = new Entry(id, version, reader.ReadBytes(length));
            Validate(entry);
            if (!entries.TryAdd(id, entry)) throw new InvalidDataException("Duplicate durable record identity.");
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing durable registry data.");
        return new(content.Version, origin, entries);
    }

    private static byte[] Encode(SortedDictionary<Guid, Entry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Header);
            writer.Write(entries.Count);
            foreach (var entry in entries.Values)
            {
                Validate(entry);
                writer.Write(entry.Id.ToByteArray());
                writer.Write(entry.Version);
                writer.Write(entry.Payload.Length);
                writer.Write(entry.Payload);
                if (stream.Length > MaximumRegistryBytes) throw new InvalidOperationException("The durable registry byte budget is full.");
            }
        }
        return stream.ToArray();
    }

    private static void Validate(Entry entry)
    {
        if (entry.Id == Guid.Empty || entry.Version <= 0 || entry.Payload.Length is <= 0 or > MaximumEntryBytes)
            throw new InvalidDataException("Invalid durable registry record.");
    }
}
