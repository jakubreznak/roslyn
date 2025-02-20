﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Immutable;
using Microsoft.CodeAnalysis.Editor.Shared.Tagging;
using Microsoft.CodeAnalysis.Editor.Shared.Utilities;
using Microsoft.CodeAnalysis.Editor.Tagging;
using Microsoft.CodeAnalysis.Options;
using Microsoft.CodeAnalysis.Shared.Collections;
using Microsoft.CodeAnalysis.Shared.TestHooks;
using Microsoft.CodeAnalysis.Workspaces;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Tagging;

namespace Microsoft.CodeAnalysis.Diagnostics;

/// <summary>
/// Base type of all diagnostic taggers (classification, squiggles, suggestions, inline-diags).  Subclasses can control
/// things by overriding functionality in this type.  Internally, this will switch to either a pull or push cased
/// approach at instantiation time depending on our internal feature flag.
/// </summary>
internal abstract partial class AbstractDiagnosticsTaggerProvider<TTag> : ITaggerProvider
    where TTag : ITag
{
    protected readonly IGlobalOptionService GlobalOptions;

    /// <summary>
    /// Underlying diagnostic tagger responsible for the syntax/semantic and compiler/analyzer split.  The ordering of
    /// these taggers is not relevant.  They are not executed serially.  Rather, they all run concurrently, notifying us
    /// (potentially concurrently as well) when change occur.
    /// </summary>
    private readonly ImmutableArray<SingleDiagnosticKindPullTaggerProvider> _diagnosticsTaggerProviders;

    public AbstractDiagnosticsTaggerProvider(
        IThreadingContext threadingContext,
        IDiagnosticService diagnosticService,
        IDiagnosticAnalyzerService analyzerService,
        IGlobalOptionService globalOptions,
        ITextBufferVisibilityTracker? visibilityTracker,
        IAsynchronousOperationListener listener)
    {
        GlobalOptions = globalOptions;

        _diagnosticsTaggerProviders =
        [
            CreateDiagnosticsTaggerProvider(DiagnosticKind.CompilerSyntax),
            CreateDiagnosticsTaggerProvider(DiagnosticKind.CompilerSemantic),
            CreateDiagnosticsTaggerProvider(DiagnosticKind.AnalyzerSyntax),
            CreateDiagnosticsTaggerProvider(DiagnosticKind.AnalyzerSemantic),
        ];

        return;

        SingleDiagnosticKindPullTaggerProvider CreateDiagnosticsTaggerProvider(DiagnosticKind diagnosticKind)
            => new(this, diagnosticKind, threadingContext, diagnosticService, analyzerService, globalOptions, visibilityTracker, listener);
    }

    // Functionality for subclasses to control how this diagnostic tagging operates.  All the individual
    // SingleDiagnosticKindTaggerProvider will defer to these to do the work so that they otherwise operate
    // identically.

    protected abstract ImmutableArray<IOption2> Options { get; }
    protected virtual ImmutableArray<IOption2> FeatureOptions { get; } = [];

    protected abstract bool IncludeDiagnostic(DiagnosticData data);

    protected abstract bool TagEquals(TTag tag1, TTag tag2);

    protected abstract TTag? CreateTag(Workspace workspace, DiagnosticData diagnostic);

    /// <summary>
    /// Get the <see cref="DiagnosticDataLocation"/> that should have the tag applied to it.
    /// In most cases, this is the <see cref="DiagnosticData.DataLocation"/> but overrides can change it (e.g. unnecessary classifications).
    /// </summary>
    /// <param name="diagnosticData">the diagnostic containing the location(s).</param>
    /// <returns>an array of locations that should have the tag applied.</returns>
    protected virtual ImmutableArray<DiagnosticDataLocation> GetLocationsToTag(DiagnosticData diagnosticData)
        => diagnosticData.DataLocation is not null ? [diagnosticData.DataLocation] : [];

    public ITagger<T>? CreateTagger<T>(ITextBuffer buffer) where T : ITag
    {
        using var taggers = TemporaryArray<EfficientTagger<TTag>>.Empty;
        foreach (var taggerProvider in _diagnosticsTaggerProviders)
        {
            var innerTagger = taggerProvider.CreateTagger(buffer);
            if (innerTagger != null)
                taggers.Add(innerTagger);
        }

        var tagger = new SimpleAggregateTagger<TTag>(taggers.ToImmutableAndClear());
        if (tagger is not ITagger<T> genericTagger)
        {
            tagger.Dispose();
            return null;
        }

        return genericTagger;
    }

    private static ITaggerEventSource CreateEventSourceWorker(ITextBuffer subjectBuffer, IDiagnosticService diagnosticService)
    {
        // OnTextChanged is added for diagnostics in source generated files: it's possible that the analyzer driver
        // executed on content which was produced by a source generator but is not yet reflected in an open text
        // buffer for that generated file. In this case, we need to update the tags after the buffer updates (which
        // triggers a text changed event) to ensure diagnostics are positioned correctly.
        return TaggerEventSources.Compose(
            TaggerEventSources.OnDocumentActiveContextChanged(subjectBuffer),
            TaggerEventSources.OnWorkspaceRegistrationChanged(subjectBuffer),
            TaggerEventSources.OnDiagnosticsChanged(subjectBuffer, diagnosticService),
            TaggerEventSources.OnTextChanged(subjectBuffer));
    }

    protected ITagSpan<TTag>? CreateTagSpan(Workspace workspace, SnapshotSpan span, DiagnosticData data)
    {
        var errorTag = CreateTag(workspace, data);
        if (errorTag == null)
            return null;

        // Ensure the diagnostic has at least length 1.  Tags must have a non-empty length in order to actually show
        // up in the editor.
        var adjustedSpan = AdjustSnapshotSpan(span, minimumLength: 1);
        if (adjustedSpan.Length == 0)
            return null;

        return new TagSpan<TTag>(adjustedSpan, errorTag);
    }

    protected virtual SnapshotSpan AdjustSnapshotSpan(SnapshotSpan span, int minimumLength)
        => AdjustSnapshotSpan(span, minimumLength, maximumLength: int.MaxValue);

    protected static SnapshotSpan AdjustSnapshotSpan(SnapshotSpan span, int minimumLength, int maximumLength)
    {
        var snapshot = span.Snapshot;

        // new length
        var length = Math.Min(Math.Max(span.Length, minimumLength), maximumLength);

        // make sure start + length is smaller than snapshot.Length and start is >= 0
        var start = Math.Max(0, Math.Min(span.Start, snapshot.Length - length));

        // make sure length is smaller than snapshot.Length which can happen if start == 0
        return new SnapshotSpan(snapshot, start, Math.Min(start + length, snapshot.Length) - start);
    }
}
