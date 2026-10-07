// SPDX-License-Identifier: AGPL-3.0-only
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ArcForges.ArcScope.Core.Application;
using ArcForges.Capabilities;
using ArcForges.Contracts.Foundation.V1;
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Contracts.PublicApi.V1;
using ArcForges.Foundation;
using ArcForges.Foundation.Errors;
using ArcForges.Persistence.Sqlite;
using Google.Protobuf;

namespace ArcForges.ArcScope.Core.Infrastructure;

/// <summary>Durable reservation and exact outcome replay; an unfinished reservation never dispatches again after restart.</summary>
public sealed class ProductInvocationRecordStore : IInvocationRecordStore, IDisposable
{
    private const string Header = "ArcForges.ArcScope.InvocationRecord.v1";
    private const int MaximumBytes = 1024 * 1024;
    private readonly DurableRegistry registry;
    private readonly Dictionary<string, CapabilityInvocationBinding> responseBindings;
    private readonly ConcurrentDictionary<Guid, Reservation> byCommand = new();
    private readonly ConcurrentDictionary<Guid, Reservation> byReservation = new();
    private readonly object lifecycle = new();
    private int disposed;

    private sealed record Reservation(Guid Command, Guid Id, byte[] Fingerprint)
    {
        internal TaskCompletionSource<InvocationOutcome> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private sealed record Record(Guid Reservation, byte[] Fingerprint, byte[]? Outcome);

    public ProductInvocationRecordStore(IStore store, Guid partitionId, UserId actor, IClock clock,
        CapabilityRegistry catalogue, IEnumerable<CapabilityInvocationBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(catalogue);
        ArgumentNullException.ThrowIfNull(bindings);
        registry = new(store, "arcscope.capability.invocations.v1", partitionId, actor, clock);
        responseBindings = new(StringComparer.Ordinal);
        foreach (var binding in bindings)
        {
            ArgumentNullException.ThrowIfNull(binding);
            var registration = catalogue.Find(binding.CapabilityKey)
                ?? throw new ArgumentException("A journal binding must belong to the exact registered catalogue.", nameof(bindings));
            if (!responseBindings.TryAdd(registration.Descriptor.ResponseSchema, binding))
                throw new ArgumentException("Response schemas must identify one exact binding.", nameof(bindings));
        }
        if (responseBindings.Count == 0) throw new ArgumentException("At least one exact response binding is required.", nameof(bindings));
    }

    public async ValueTask<Outcome<InvocationRecordClaim>> BeginAsync(Id commandId, ByteString requestFingerprint,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        var command = UuidBoundary.FromWire(commandId);
        ArgumentNullException.ThrowIfNull(requestFingerprint);
        if (requestFingerprint.Length != 32) return Failure<InvocationRecordClaim>("validation.invalid_request");
        var fingerprint = requestFingerprint.ToByteArray();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = registry.Read(command);
            if (entry is not null) return await ReplayAsync(command, DecodeRecord(entry), fingerprint, cancellationToken).ConfigureAwait(false);
            var candidate = new Reservation(command, Guid.NewGuid(), fingerprint);
            bool registered;
            lock (lifecycle)
            {
                ObjectDisposedException.ThrowIf(disposed != 0, this);
                registered = byCommand.TryAdd(command, candidate);
                if (registered && !byReservation.TryAdd(candidate.Id, candidate))
                {
                    byCommand.TryRemove(command, out _);
                    throw new InvalidOperationException("Reservation identity collision.");
                }
            }
            if (!registered)
            {
                var existing = byCommand.GetValueOrDefault(command);
                if (existing is null) continue;
                if (!SameFingerprint(existing.Fingerprint, fingerprint)) return Failure<InvocationRecordClaim>("command.reused_identifier");
                return await AwaitExistingAsync(existing, cancellationToken).ConfigureAwait(false);
            }
            try
            {
                if (await registry.TryCreateAsync(new(command, 1, EncodeRecord(new(candidate.Id, fingerprint, null))), cancellationToken).ConfigureAwait(false))
                    return Outcome.Success(Volatile.Read(ref disposed) == 0
                        ? InvocationRecordClaim.ForExecution(candidate.Id)
                        : InvocationRecordClaim.ForReplay(InvocationOutcome.Cancelled(EffectCertainty.Unknown)));
            }
            catch
            {
                candidate.Completion.TrySetResult(InvocationOutcome.Cancelled(EffectCertainty.Unknown));
                byReservation.TryRemove(candidate.Id, out _);
                byCommand.TryRemove(command, out _);
                throw;
            }
            candidate.Completion.TrySetResult(InvocationOutcome.Cancelled(EffectCertainty.Unknown));
            byReservation.TryRemove(candidate.Id, out _);
            byCommand.TryRemove(command, out _);
        }
    }

    public async ValueTask<Outcome<InvocationOutcome>> CompleteAsync(InvocationRecordClaim claim, InvocationOutcome outcome,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        ArgumentNullException.ThrowIfNull(claim);
        ArgumentNullException.ThrowIfNull(outcome);
        if (claim.Kind != InvocationRecordClaimKind.Execute || !byReservation.TryGetValue(claim.ReservationId, out var reservation))
            return Failure<InvocationOutcome>("internal.unexpected");
        var entry = registry.Read(reservation.Command) ?? throw new InvalidDataException("An execution reservation disappeared.");
        var record = DecodeRecord(entry);
        if (record.Reservation != reservation.Id || !SameFingerprint(record.Fingerprint, reservation.Fingerprint))
            throw new InvalidDataException("An execution reservation's immutable identity changed.");
        var encoded = EncodeOutcome(outcome);
        if (record.Outcome is not null)
        {
            if (!record.Outcome.AsSpan().SequenceEqual(encoded)) return Failure<InvocationOutcome>("command.reused_identifier");
            var previous = DecodeOutcome(record.Outcome);
            reservation.Completion.TrySetResult(previous);
            return Outcome.Success(previous);
        }
        var next = new DurableRegistry.Entry(reservation.Command, 2, EncodeRecord(record with { Outcome = encoded }));
        try
        {
            _ = await registry.TryReplaceAsync(next, 1, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The SQLite contract permits a notification to throw after commit. Re-read the exact record before returning an outcome.
            var reconciled = registry.Read(reservation.Command);
            if (reconciled is null || reconciled.Version != 2 || !reconciled.Payload.AsSpan().SequenceEqual(next.Payload))
            {
                reservation.Completion.TrySetResult(InvocationOutcome.Cancelled(EffectCertainty.Unknown));
                throw;
            }
        }
        var committed = registry.Read(reservation.Command) ?? throw new InvalidDataException("A completed invocation disappeared.");
        if (committed.Version != 2 || !committed.Payload.AsSpan().SequenceEqual(next.Payload))
        {
            if (committed.Version != 2) throw new InvalidDataException("An invocation cannot be acknowledged before durable commit.");
            reservation.Completion.TrySetResult(DecodeOutcome(DecodeRecord(committed).Outcome!));
            return Failure<InvocationOutcome>("command.reused_identifier");
        }
        var result = DecodeOutcome(DecodeRecord(committed).Outcome!);
        reservation.Completion.TrySetResult(result);
        return Outcome.Success(result);
    }

    private async ValueTask<Outcome<InvocationRecordClaim>> ReplayAsync(Guid command, Record record, byte[] fingerprint,
        CancellationToken cancellationToken)
    {
        if (!SameFingerprint(record.Fingerprint, fingerprint)) return Failure<InvocationRecordClaim>("command.reused_identifier");
        if (record.Outcome is not null) return Outcome.Success(InvocationRecordClaim.ForReplay(DecodeOutcome(record.Outcome)));
        if (byCommand.TryGetValue(command, out var local) && local.Id == record.Reservation)
            return await AwaitExistingAsync(local, cancellationToken).ConfigureAwait(false);
        // Another process's unfinished command, or a crashed owner, has an unknown effect. It is never a new execution claim.
        return Outcome.Success(InvocationRecordClaim.ForReplay(InvocationOutcome.Cancelled(EffectCertainty.Unknown)));
    }

    private static async ValueTask<Outcome<InvocationRecordClaim>> AwaitExistingAsync(Reservation reservation,
        CancellationToken cancellationToken)
    {
        try
        {
            var outcome = await reservation.Completion.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            return Outcome.Success(InvocationRecordClaim.ForReplay(outcome));
        }
        catch (TimeoutException)
        {
            return Outcome.Success(InvocationRecordClaim.ForReplay(InvocationOutcome.Cancelled(EffectCertainty.Unknown)));
        }
    }

    private byte[] EncodeOutcome(InvocationOutcome outcome)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write((int)outcome.Kind);
            switch (outcome.Kind)
            {
                case OutcomeKind.Success:
                    var value = outcome.Value ?? throw new InvalidDataException("A successful invocation has no value.");
                    _ = Restore(value.Result, value.Version);
                    Bytes(writer, value.Result.ToByteArray());
                    writer.Write((int)value.Version.Kind);
                    writer.Write(value.Version.Kind switch
                    {
                        InvocationResultVersionKind.Revision => checked((ulong)value.Version.Revision!.Value),
                        InvocationResultVersionKind.NativeContentRev => value.Version.NativeContentRev!.Value,
                        InvocationResultVersionKind.NonVersioned => 0UL,
                        _ => throw new InvalidDataException("Unknown successful owner-version kind."),
                    });
                    break;
                case OutcomeKind.Failure:
                    Bytes(writer, (outcome.Failure ?? throw new InvalidDataException("A failed invocation has no error.")).ToWire().ToByteArray());
                    break;
                case OutcomeKind.Cancelled:
                    writer.Write((int)outcome.CancellationEffect);
                    break;
                default:
                    throw new InvalidDataException("Unknown invocation outcome kind.");
            }
        }
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Invocation outcome exceeds its byte budget.");
        return stream.ToArray();
    }

    private InvocationOutcome DecodeOutcome(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        InvocationOutcome outcome;
        switch ((OutcomeKind)reader.ReadInt32())
        {
            case OutcomeKind.Success:
                var result = ParseStored(CapabilityResult.Parser, Bytes(reader));
                var kind = (InvocationResultVersionKind)reader.ReadInt32();
                var number = reader.ReadUInt64();
                var version = kind switch
                {
                    InvocationResultVersionKind.Revision => InvocationResultVersion.FromRevision(new ArcForges.Contracts.Foundation.V1.Revision { Value = checked((long)number) }),
                    InvocationResultVersionKind.NativeContentRev => InvocationResultVersion.FromNativeContentRev(new NativeContentRev { Value = number }),
                    InvocationResultVersionKind.NonVersioned when number == 0 => InvocationResultVersion.NonVersioned(),
                    _ => throw new InvalidDataException("Invalid persisted owner-version union."),
                };
                outcome = InvocationOutcome.Success(Restore(result, version));
                break;
            case OutcomeKind.Failure:
                var failure = TypedFailure.FromWire(ParseStored(ArcError.Parser, Bytes(reader)));
                if (!failure.IsKnownCode) throw new InvalidDataException("An unregistered stored error cannot become a producer result.");
                outcome = InvocationOutcome.FailureResult(failure);
                break;
            case OutcomeKind.Cancelled:
                outcome = InvocationOutcome.Cancelled((EffectCertainty)reader.ReadInt32());
                break;
            default:
                throw new InvalidDataException("Unknown stored outcome kind.");
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Trailing stored invocation outcome.");
        return outcome;
    }

    private CapabilityInvocationValue Restore(CapabilityResult result, InvocationResultVersion version)
    {
        var nativeVersion = AnnotationOperationCodec.NativeVersionOfResult(result);
        if (version.Kind != InvocationResultVersionKind.NativeContentRev || version.NativeContentRev?.Value != nativeVersion)
            throw new InvalidDataException("The stored result payload and explicit owner-version union must agree exactly.");
        if (!result.HasSchemaId || !responseBindings.TryGetValue(result.SchemaId, out var binding))
            throw new InvalidDataException("An unknown response schema cannot be restored.");
        var restored = binding.RestoreResult(result, version);
        if (!restored.TryGetValue(out var value) || value is null) throw new InvalidDataException("Stored result violates its exact binding contract.");
        return value;
    }

    private static byte[] EncodeRecord(Record record)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, new UTF8Encoding(false, true), leaveOpen: true))
        {
            writer.Write(Header);
            writer.Write(record.Reservation.ToByteArray());
            writer.Write(record.Fingerprint);
            writer.Write(record.Outcome is not null);
            if (record.Outcome is not null) Bytes(writer, record.Outcome);
        }
        if (stream.Length > MaximumBytes) throw new InvalidDataException("Invocation record exceeds its byte budget.");
        return stream.ToArray();
    }

    private static Record DecodeRecord(DurableRegistry.Entry entry)
    {
        using var stream = new MemoryStream(entry.Payload, writable: false);
        using var reader = new BinaryReader(stream, new UTF8Encoding(false, true));
        if (reader.ReadString() != Header) throw new InvalidDataException("Unsupported invocation record format.");
        var id = reader.ReadBytes(16);
        var fingerprint = reader.ReadBytes(32);
        if (id.Length != 16 || fingerprint.Length != 32) throw new EndOfStreamException();
        var presence = reader.ReadByte();
        if (presence > 1) throw new InvalidDataException("Invalid stored invocation outcome presence.");
        var outcome = presence == 1 ? Bytes(reader) : null;
        if (stream.Position != stream.Length || entry.Version != (outcome is null ? 1 : 2) || new Guid(id) == Guid.Empty)
            throw new InvalidDataException("Invalid invocation reservation lifecycle or trailing data.");
        return new(new Guid(id), fingerprint, outcome);
    }

    private static void Bytes(BinaryWriter writer, byte[] value) { writer.Write(value.Length); writer.Write(value); }
    private static byte[] Bytes(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length is <= 0 or > MaximumBytes || length > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Invalid persisted invocation field byte budget.");
        return reader.ReadBytes(length);
    }
    private static bool SameFingerprint(byte[] left, byte[] right) => CryptographicOperations.FixedTimeEquals(left, right);
    private static T ParseStored<T>(MessageParser<T> parser, byte[] bytes) where T : IMessage<T>
    {
        using var stream = new MemoryStream(bytes, writable: false);
        using var input = CodedInputStream.CreateWithLimits(stream, MaximumBytes, 32);
        return parser.ParseFrom(input);
    }
    private static Outcome<T> Failure<T>(string code) => Outcome.Failure<T>(TypedFailure.Create(code));

    public void Dispose()
    {
        lock (lifecycle)
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            foreach (var reservation in byCommand.Values)
                reservation.Completion.TrySetResult(InvocationOutcome.Cancelled(EffectCertainty.Unknown));
            byReservation.Clear();
            byCommand.Clear();
        }
    }
}
