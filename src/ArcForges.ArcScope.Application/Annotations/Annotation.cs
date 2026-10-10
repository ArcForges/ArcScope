// SPDX-License-Identifier: AGPL-3.0-only
using ArcForges.Contracts.Foundation.Values;
using ArcForges.Foundation;

namespace ArcForges.ArcScope.Application.Annotations;

/// <summary>
/// One committed change of an annotation: the text it appended, who authored it, its content-origin kind, when it
/// committed and the command that produced it. The command fingerprint lets a retried command be recognised as a replay
/// after a restart without repeating its effect.
/// </summary>
public sealed class AnnotationEntry
{
    public AnnotationEntry(NativeRevision revision, AnnotationText text, AnnotationActor actor, AnnotationContentKind contentKind,
        Instant committedAt, CommandId command, string commandFingerprint)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(actor);
        if (revision.Value == 0) throw new ArgumentOutOfRangeException(nameof(revision), "A committed entry has a positive revision.");
        if (!AnnotationContentKinds.IsDefined(contentKind)) throw new ArgumentOutOfRangeException(nameof(contentKind));
        _ = command.ToWire();
        if (!AnnotationCommandFingerprint.IsWellFormed(commandFingerprint))
            throw new ArgumentException("A command fingerprint is 64 lowercase hexadecimal characters.", nameof(commandFingerprint));
        Revision = revision;
        Text = text;
        Actor = actor;
        ContentKind = contentKind;
        CommittedAt = committedAt;
        Command = command;
        CommandFingerprint = commandFingerprint;
    }

    public NativeRevision Revision { get; }
    public AnnotationText Text { get; }
    public AnnotationActor Actor { get; }
    public AnnotationContentKind ContentKind { get; }
    public Instant CommittedAt { get; }
    public CommandId Command { get; }
    public string CommandFingerprint { get; }
}

/// <summary>
/// An annotation: authored content with its own identity and append-only history, attached to a sample range of one
/// capture session and never written into the raw capture. Revision <c>n</c> is the state after the <c>n</c>th entry; an
/// append adds one entry and never rewrites an earlier one. Instances are immutable.
/// </summary>
public sealed class Annotation
{
    /// <summary>The largest history one annotation may hold.</summary>
    public const int MaxEntries = 1024;

    private readonly AnnotationEntry[] entries;

    /// <summary>Rebuilds a stored annotation, refusing any history that breaks the annotation invariants.</summary>
    public Annotation(AnnotationId id, SessionTargetId session, SampleRange range, IEnumerable<AnnotationEntry> history)
    {
        ArgumentNullException.ThrowIfNull(history);
        _ = id.ToWire();
        _ = session.ToWire();
        if (!range.IsInitialized) throw new ArgumentException("An annotation covers an initialized sample range.", nameof(range));
        entries = history.ToArray();
        if (entries.Length is 0 or > MaxEntries)
            throw new ArgumentException("An annotation holds between one and " + MaxEntries + " entries.", nameof(history));
        long bytes = 0;
        var commands = new HashSet<CommandId>();
        for (int index = 0; index < entries.Length; index++)
        {
            var entry = entries[index] ?? throw new ArgumentException("An annotation history has no missing entry.", nameof(history));
            if (entry.Revision.Value != (ulong)index + 1)
                throw new ArgumentException("Annotation revisions are contiguous from one.", nameof(history));
            if (!commands.Add(entry.Command))
                throw new ArgumentException("One command produces at most one annotation entry.", nameof(history));
            bytes += entry.Text.Utf8ByteCount;
        }

        if (bytes > AnnotationText.MaxUtf8Bytes)
            throw new ArgumentException("An annotation's text fits the 256 KiB Contracts Text bound.", nameof(history));
        Id = id;
        Session = session;
        Range = range;
        TextUtf8ByteCount = bytes;
    }

    public AnnotationId Id { get; }
    public SessionTargetId Session { get; }
    public SampleRange Range { get; }

    /// <summary>The append-only history, oldest first.</summary>
    public IReadOnlyList<AnnotationEntry> Entries => entries;

    /// <summary>The product-owned content revision (the Contracts <c>NativeContentRev</c>).</summary>
    public NativeRevision Revision => entries[^1].Revision;

    /// <summary>The total UTF-8 size of the text over the whole history.</summary>
    public long TextUtf8ByteCount { get; }

    /// <summary>The content-origin kind union over the history, in canonical carrier order.</summary>
    public IReadOnlyList<AnnotationContentKind> OriginKinds =>
        entries.Select(entry => entry.ContentKind).Distinct().Order().ToArray();

    /// <summary>Finds the entry a command produced, if that command already committed against this annotation.</summary>
    public AnnotationEntry? FindEntry(CommandId command) => Array.Find(entries, entry => entry.Command == command);

    /// <summary>True when one more entry of <paramref name="textUtf8Bytes"/> still fits the history and size bounds.</summary>
    public bool CanAppend(int textUtf8Bytes) =>
        entries.Length < MaxEntries && TextUtf8ByteCount + textUtf8Bytes <= AnnotationText.MaxUtf8Bytes;

    /// <summary>The next state with one more entry; the caller has already checked the expected revision.</summary>
    internal Annotation Append(AnnotationEntry entry) => new(Id, Session, Range, entries.Append(entry));
}
