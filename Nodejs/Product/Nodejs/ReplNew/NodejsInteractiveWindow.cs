/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.NodejsTools.Repl;
using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Language.Intellisense;
using Microsoft.VisualStudio.InteractiveWindow;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Text.Projection;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;
using System.IO;

#if NTVS_FEATURE_INTERACTIVEWINDOW
using Microsoft.VisualStudio;
namespace Microsoft.NodejsTools.Repl {
#else
namespace Microsoft.VisualStudio.Repl {
#endif
    /// <summary>
    /// Provides implementation of a Repl Window built on top of the VS editor using projection buffers.
    /// 
    /// TODO: We should condense committed language buffers into a single language buffer and save the
    /// classifications from the previous language buffer if the perf of having individual buffers
    /// starts having problems w/ a large number of inputs.
    /// </summary>
    [Guid(NodejsInteractiveWindow.TypeGuid)]
    class NodejsInteractiveWindow : IInteractiveWindow {
#if NTVS_FEATURE_INTERACTIVEWINDOW
        public const string TypeGuid = "2153A414-267E-4731-B891-E875ADBA1993";
#else
        public const string TypeGuid = "5adb6033-611f-4d39-a193-57a717115c0f";
#endif
#pragma warning disable 0649
#pragma warning disable 0169
        private bool _adornmentToMinimize = false;
        private bool _showOutput, _useSmartUpDown;
        
        // true if code is being executed:
        private bool _isRunning;

        private Stopwatch _sw;
        private DispatcherTimer _executionTimer;
        private Cursor _oldCursor;
        private List<IReplCommand> _commands;
        private IWpfTextViewHost _textViewHost;
        private IEditorOperations _editorOperations;
        private readonly History/*!*/ _history;
        private TaskScheduler _uiScheduler;
        private PropertyCollection _properties;

        //
        // Services
        // 
        private readonly IComponentModel/*!*/ _componentModel;
        private readonly Guid _langSvcGuid;
        private readonly string _replId;
        private readonly IContentType/*!*/ _languageContentType;
        private readonly string[] _roles;
        private readonly IClassifierAggregatorService _classifierAgg;
        private ReplAggregateClassifier _primaryClassifier;
        //private readonly IReplEvaluator/*!*/ _evaluator;
        private readonly IInteractiveEvaluator _evaluator;
        private IVsFindTarget _findTarget;
        private IVsTextView _view;
        private ISmartIndent _languageIndenter;

        //
        // Command filter chain: 
        // window -> VsTextView -> ... -> pre-language -> language services -> post-language -> editor services -> preEditor -> editor
        //
        private IOleCommandTarget _preLanguageCommandFilter;
        private IOleCommandTarget _languageServiceCommandFilter;
        private IOleCommandTarget _postLanguageCommandFilter;
        private IOleCommandTarget _preEditorCommandFilter;
        private IOleCommandTarget _editorServicesCommandFilter;
        private IOleCommandTarget _editorCommandFilter;

        //
        // A list of scopes if this REPL is multi-scoped
        // 
        private string[] _currentScopes;
        private bool _scopeListVisible;

        //
        // Buffer composition.
        // 
        private readonly ITextBufferFactoryService _bufferFactory;                          // Factory for creating output, std input, prompt and language buffers.
        private IProjectionBuffer _projectionBuffer;
        private ITextBuffer _outputBuffer;
        private ITextBuffer _stdInputBuffer;
        private ITextBuffer _currentLanguageBuffer;
        private string _historySearch;
        private readonly string _lineBreakString;

        // Read-only regions protecting initial span of the corresponding buffers:
        private IReadOnlyRegion[] _stdInputProtection = new IReadOnlyRegion[2];
        private IReadOnlyRegion[] _outputProtection = new IReadOnlyRegion[2];
        
        // List of projection buffer spans - the projection buffer doesn't allow us to enumerate spans so we need to track them manually:
        private readonly List<ReplSpan> _projectionSpans = new List<ReplSpan>();

        // Maps line numbers in projection buffer to indices of projection spans corresponding to primary and stdin prompts.
        // All but the last item correspond to submitted prompts that never change their line position.
        private List<KeyValuePair<int, int>> _promptLineMapping = new List<KeyValuePair<int, int>>();                      

        //
        // Submissions.
        //

        // Pending submissions to be processed whenever the REPL is ready to accept submissions.
        private Queue<string> _pendingSubmissions;

        //
        // Standard input.
        //

        // non-null if reading from stdin - position in the _inputBuffer where we map stdin
        private int? _stdInputStart;
        private bool _readingStdIn;
        private int _currentInputId = 1;
        private string _inputValue;
        private string _uncommittedInput;
        private readonly AutoResetEvent _inputEvent = new AutoResetEvent(false);
        
        //
        // Output buffering.
        //
        private readonly OutputBuffer _buffer;
        private readonly List<ColoredSpan> _outputColors = new List<ColoredSpan>();
        private bool _addedLineBreakOnLastOutput;

        private string _commandPrefix = "%";
        private string _prompt = "» ";        // prompt for primary input
        private string _secondPrompt = String.Empty;    // prompt for 2nd and additional lines
        private string _stdInputPrompt = String.Empty;  // prompt for standard input
        private bool _displayPromptInMargin, _formattedPrompts;

        private static readonly char[] _whitespaceChars = new[] { '\r', '\n', ' ', '\t' };
        private const string _boxSelectionCutCopyTag = "MSDEVColumnSelect";

        //
        //fields from OutputBuffer.cs
        //
        private readonly DispatcherTimer _timer;
        private int _maxSize;
        private readonly object _lock;
        private int _bufferLength;
        private long _lastFlush;
        private readonly List<OutputBuffer.OutputEntry> _outputEntries = new List<OutputBuffer.OutputEntry>();
#pragma warning restore 0649
#pragma warning restore 0169

        public NodejsInteractiveWindow(IComponentModel/*!*/ model, IInteractiveEvaluator/*!*/ evaluator, IContentType/*!*/ contentType, string[] roles, string/*!*/ title, Guid languageServiceGuid, string replId) {
            Contract.Assert(evaluator != null);
            Contract.Assert(contentType != null);
            Contract.Assert(title != null);
            Contract.Assert(model != null);
            
            _properties = new PropertyCollection();

            _replId = replId;
            _langSvcGuid = languageServiceGuid;
            _buffer = new OutputBuffer(this);

            _componentModel = model;
            _evaluator = evaluator;
            _languageContentType = contentType;
            _roles = roles;
            
            Contract.Requires(_commandPrefix != null && _prompt != null);
            
            _history = new History();

            _bufferFactory = model.GetService<ITextBufferFactoryService>();
            _classifierAgg = model.GetService<IClassifierAggregatorService>();

            _showOutput = true;
        }

        private bool IsCommandApplicable(IReplCommand command) {
            bool applicable = true;
            string[] commandRoles = command.GetType().GetCustomAttributes(typeof(ReplRoleAttribute), true).Select(r => ((ReplRoleAttribute)r).Name).ToArray();
            if (_roles.Length > 0) {
                // The window has one or more roles, so the following commands will be applicable:
                // - commands that don't specify a role
                // - commands that specify a role that matches one of the window roles
                if (commandRoles.Length > 0) {
                    applicable = _roles.Intersect(commandRoles).Count() > 0;
                }
            } else {
                // The window doesn't have any role, so the following commands will be applicable:
                // - commands that don't specify any role
                applicable = commandRoles.Length == 0;
            }

            return applicable;
        }

        #region Misc Helpers

        public IEditorOperations EditorOperations { 
            get { return _editorOperations; }
        }

        public ITextBuffer/*!*/ TextBuffer {
            get { return TextView.TextBuffer; }
        }

        public ITextSnapshot CurrentSnapshot {
            get { return TextBuffer.CurrentSnapshot; }
        }

        private void RequiresLanguageBuffer() {
            if (_currentLanguageBuffer == null) {
                throw new InvalidOperationException("Language buffer not available");
            }
        }
        
        #endregion

        #region IReplWindow

        public event Action ReadyForInput;

        /// <summary>
        /// See IReplWindow
        /// </summary>
        public IWpfTextView/*!*/ TextView {
            get { 
                return _textViewHost.TextView; 
            }
        }

        /// <summary>
        /// See IReplWindow
        /// </summary>
        public IInteractiveEvaluator/*!*/ Evaluator {
            get {
                return _evaluator;
            }
        }

        public void ClearHistory() {
            if (!CheckAccess()) {
                Dispatcher.Invoke(new Action(ClearHistory));
                return;
            }
            _history.Clear();
        }

        /// <summary>
        /// See IReplWindow
        /// </summary>
        public void ClearScreen() {
            ClearScreen(insertInputPrompt: false);
        }

        private void ClearScreen(bool insertInputPrompt) {
            if (!CheckAccess()) {
                Dispatcher.Invoke(new Action(ClearScreen));
                return;
            }

            if (_stdInputStart != null) {
                CancelStandardInput();
            }

            _adornmentToMinimize = false;
            InlineReplAdornmentProvider.RemoveAllAdornments(TextView);

            // remove all the spans except our initial span from the projection buffer
            _promptLineMapping = new List<KeyValuePair<int,int>>();
            _currentInputId = 1;
            _uncommittedInput = null;
            _outputColors.Clear();

            // Clear the projection and buffers last as this might trigger events that might access other state of the REPL window:
            RemoveProtection(_outputBuffer, _outputProtection);
            RemoveProtection(_stdInputBuffer, _stdInputProtection);

            using (var edit = _outputBuffer.CreateEdit(EditOptions.None, null, SuppressPromptInjectionTag)) {
                edit.Delete(0, _outputBuffer.CurrentSnapshot.Length);
                edit.Apply();
            }
            _addedLineBreakOnLastOutput = false;
            using (var edit = _stdInputBuffer.CreateEdit(EditOptions.None, null, SuppressPromptInjectionTag)) {
                edit.Delete(0, _stdInputBuffer.CurrentSnapshot.Length);
                edit.Apply();
            }

            ClearProjection();

            if (insertInputPrompt) {
                PrepareForInput();
            }
        }

        /// <summary>
        /// See IReplWindow
        /// </summary>
        public void Focus() {
            var textView = TextView;

            IInputElement input = textView as IInputElement;
            if (input != null) {
                Keyboard.Focus(input);
            }
        }

        public void InsertCode(string text) {
            if (!CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => InsertCode(text)));
                return;
            }

            if (_stdInputStart == null) {
                if (_isRunning) {
                    AppendUncommittedInput(text);
                } else {
                    if (!TextView.Selection.IsEmpty) {
                        CutOrDeleteSelection(false);
                    }
                    _editorOperations.InsertText(text);
                }
            }
        }

        public void Submit(IEnumerable<string> inputs) {
            if (!CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => Submit(inputs)));
                return;
            }

            if (_stdInputStart == null) {
                if (!_isRunning && _currentLanguageBuffer != null) {
                    StoreUncommittedInput();
                    PendSubmissions(inputs);
                    ProcessPendingSubmissions();
                } else {
                    PendSubmissions(inputs);
                }
            }
        }

        private void PendSubmissions(IEnumerable<string> inputs) {
            if (_pendingSubmissions == null) {
                _pendingSubmissions = new Queue<string>();
            }

            foreach (var input in inputs) {
                _pendingSubmissions.Enqueue(input);
            }
        }
        
        /// <summary>
        /// See IReplWindow
        /// </summary>
        public Task<VisualStudio.InteractiveWindow.ExecutionResult> Reset() {
            if (_stdInputStart != null) {
                UIThread(CancelStandardInput);
            }
            
            return Evaluator.ResetAsync().
                ContinueWith(completed => {
                    // flush output produced by the process before it was killed:
                    _buffer.Flush();

                    return completed.Result;
                }, _uiScheduler);
        }

        public void AbortCommand() {
            if (_isRunning) {
                Evaluator.AbortExecution();
            } else {
                UIThread(() => {
                    // finish line of the current std input buffer or language buffer:
                    if (InStandardInputRegion(new SnapshotPoint(CurrentSnapshot, CurrentSnapshot.Length))) {
                        CancelStandardInput();
                    } else if (_currentLanguageBuffer != null) {
                        AppendLineNoPromptInjection(_currentLanguageBuffer);
                        PrepareForInput();
                    }
                });
            }
        }

        private T CheckOption<T>(ReplOptions option, object o) {
            if (!(o is T)) {
                throw new InvalidOperationException(String.Format(
                    "Got wrong type ({0}) for option {1}",
                    o == null ? "null" : o.GetType().Name,
                    option.ToString())
                );
            }

            return (T)o;
        }

        internal string GetPromptText(ReplSpanKind kind)
        {
            switch (kind)
            {
                case ReplSpanKind.Prompt:
                    return _prompt;

                case ReplSpanKind.SecondaryPrompt:
                    return _secondPrompt;

                case ReplSpanKind.StandardInputPrompt:
                    return _stdInputPrompt;

                default:
                    throw new InvalidOperationException();
            }
        }

        internal Control/*!*/ HostControl
        {
            get { return _textViewHost.HostControl; }
        }

#pragma warning disable 0067
        /// <summary>
        /// Trigerred when prompt margin visibility should change.
        /// </summary>
        internal event Action MarginVisibilityChanged;
        public event EventHandler<SubmissionBufferAddedEventArgs> SubmissionBufferAdded;
#pragma warning restore 0067

        internal bool DisplayPromptInMargin
        {
            get { return _displayPromptInMargin; }
        }

        #endregion

        #region Commands

        /// <summary>
        /// Clears the current input
        /// </summary>
        public void Cancel() {
            ClearInput();
            _editorOperations.MoveToEndOfDocument(false);
            _uncommittedInput = null;
        }

        private void HistoryPrevious(string search = null) {
            RequiresLanguageBuffer();

            var previous = _history.GetPrevious(search);
            if (previous != null) {
                if (String.IsNullOrWhiteSpace(search)) {
                    // don't store search as an uncommited history item
                    StoreUncommittedInputForHistory();
                }
                SetActiveCode(previous);
            }
        }

        private void HistoryNext(string search = null) {
            RequiresLanguageBuffer();

            var next = _history.GetNext(search);
            if (next != null) {
                if (String.IsNullOrWhiteSpace(search)) {
                    // don't store search as an uncommited history item
                    StoreUncommittedInputForHistory();
                }
                SetActiveCode(next);
            } else {
                string code = _history.UncommittedInput;
                _history.UncommittedInput = null;
                if (!String.IsNullOrEmpty(code)) {
                    SetActiveCode(code);
                }
            }
        }

        public void SearchHistoryPrevious() {
            if (_historySearch == null) {
                _historySearch = GetActiveCode();
            }

            HistoryPrevious(_historySearch);
        }

        public void SearchHistoryNext() {
            RequiresLanguageBuffer();

            if (_historySearch == null) {
                _historySearch = GetActiveCode();
            }

            HistoryNext(_historySearch);
        }

        private void StoreUncommittedInputForHistory() {
            if (_history.UncommittedInput == null) {
                string activeCode = GetActiveCode();
                if (activeCode.Length > 0) {
                    _history.UncommittedInput = activeCode;
                }
            }
        }

        public static NodejsInteractiveWindow FromBuffer(ITextBuffer buffer)
        {
            object result;
            buffer.Properties.TryGetProperty(typeof(NodejsInteractiveWindow), out result);
            return result as NodejsInteractiveWindow;
        }

        /// <summary>
        /// Moves to the beginning of the line.
        /// </summary>
        private void Home(bool extendSelection) {
            var caret = Caret;

            // map the end of the current language line (if applicable).
            var langLineEndPoint = TextView.BufferGraph.MapDownToFirstMatch(
                caret.Position.BufferPosition.GetContainingLine().End,
                PointTrackingMode.Positive,
                x => x.TextBuffer.ContentType == _languageContentType,
                PositionAffinity.Successor);

            if (langLineEndPoint == null) {
                // we're on some random line that doesn't include language buffer, just go to the start of the buffer
                _editorOperations.MoveToStartOfLine(extendSelection);
            } else {
                var projectionLine = caret.Position.BufferPosition.GetContainingLine();
                ITextSnapshotLine langLine = langLineEndPoint.Value.Snapshot.GetLineFromPosition(langLineEndPoint.Value.Position);

                var projectionPoint = TextView.BufferGraph.MapUpToBuffer(
                    langLine.Start, 
                    PointTrackingMode.Positive, 
                    PositionAffinity.Successor, 
                    _projectionBuffer
                );
                if (projectionPoint == null) {
                    throw new InvalidOperationException("Could not map langLine to buffer");
                }

                //
                // If the caret is already at the first non-whitespace character or
                // the line is entirely whitepsace, move to the start of the view line.
                // See (EditorOperations.MoveToHome).
                //
                // If the caret is in the prompt move the caret to the begining of the language line.
                //
                        
                int firstNonWhiteSpace = IndexOfNonWhiteSpaceCharacter(langLine);
                SnapshotPoint moveTo;
                if (firstNonWhiteSpace == -1 || 
                    projectionPoint.Value.Position + firstNonWhiteSpace == caret.Position.BufferPosition ||
                    caret.Position.BufferPosition < projectionPoint.Value.Position) {
                    moveTo = projectionPoint.Value;
                } else {
                    moveTo = projectionPoint.Value + firstNonWhiteSpace;
                }

                if (extendSelection) {
                    VirtualSnapshotPoint anchor = TextView.Selection.AnchorPoint;
                    caret.MoveTo(moveTo);
                    TextView.Selection.Select(anchor.TranslateTo(TextView.TextSnapshot), TextView.Caret.Position.VirtualBufferPosition);
                } else {
                    TextView.Selection.Clear();
                    caret.MoveTo(moveTo);
                }
            }                
        }

        /// <summary>
        /// Moves to the end of the line.
        /// </summary>
        private void End(bool extendSelection) {
            var caret = Caret;

            // map the end of the current language line (if applicable).
            var langLineEndPoint = TextView.BufferGraph.MapDownToFirstMatch(
                caret.Position.BufferPosition.GetContainingLine().End,
                PointTrackingMode.Positive,
                x => x.TextBuffer.ContentType == _languageContentType,
                PositionAffinity.Successor);

            if (langLineEndPoint == null) {
                // we're on some random line that doesn't include language buffer, just go to the start of the buffer
                _editorOperations.MoveToEndOfLine(extendSelection);
            } else {
                var projectionLine = caret.Position.BufferPosition.GetContainingLine();
                ITextSnapshotLine langLine = langLineEndPoint.Value.Snapshot.GetLineFromPosition(langLineEndPoint.Value.Position);

                var projectionPoint = TextView.BufferGraph.MapUpToBuffer(
                    langLine.End,
                    PointTrackingMode.Positive,
                    PositionAffinity.Successor,
                    _projectionBuffer
                );
                if (projectionPoint == null) {
                    throw new InvalidOperationException("Could not map langLine to buffer");
                }

                var moveTo = projectionPoint.Value;

                if (extendSelection) {
                    VirtualSnapshotPoint anchor = TextView.Selection.AnchorPoint;
                    caret.MoveTo(moveTo);
                    TextView.Selection.Select(anchor.TranslateTo(TextView.TextSnapshot), TextView.Caret.Position.VirtualBufferPosition);
                } else {
                    TextView.Selection.Clear();
                    caret.MoveTo(moveTo);
                }
            }
        }

        private void SelectAll() {
            SnapshotSpan? span = GetContainingRegion(TextView.Caret.Position.BufferPosition);

            // if the span is already selected select all text in the projection buffer:
            if (span == null || TextView.Selection.SelectedSpans.Count == 1 && TextView.Selection.SelectedSpans[0] == span.Value) {
                span = new SnapshotSpan(TextBuffer.CurrentSnapshot, new Span(0, TextBuffer.CurrentSnapshot.Length));
            }

            TextView.Selection.Select(span.Value, isReversed: false);
        }

        /// <summary>
        /// Given a point in projection buffer calculate a span that includes the point and comprises of 
        /// subsequent projection spans forming a region, i.e. a sequence of output spans in between two subsequent submissions,
        /// a language input block, or standard input block.
        /// 
        /// Internal for testing.
        /// </summary>
        internal SnapshotSpan? GetContainingRegion(SnapshotPoint point) {
            if (_promptLineMapping == null || _promptLineMapping.Count == 0 || _projectionSpans.Count == 0) {
                return null;
            }

            int closestPrecedingPrimaryPromptIndex = GetPromptMappingIndex(point.GetContainingLine().LineNumber);
            ReplSpan projectionSpan = _projectionSpans[_promptLineMapping[closestPrecedingPrimaryPromptIndex].Value + 1];

            Debug.Assert(projectionSpan.Kind == ReplSpanKind.Language || projectionSpan.Kind == ReplSpanKind.StandardInput);
            var inputSnapshot = projectionSpan.TrackingSpan.TextBuffer.CurrentSnapshot;

            // Language input block is a projection of the entire snapshot;
            // std input block is a projection of a single span:
            SnapshotPoint inputBufferEnd = (projectionSpan.Kind == ReplSpanKind.Language) ?
                new SnapshotPoint(inputSnapshot, inputSnapshot.Length) :
                projectionSpan.TrackingSpan.GetEndPoint(inputSnapshot);

            SnapshotPoint projectedInputBufferEnd = TextView.BufferGraph.MapUpToBuffer(
                inputBufferEnd,
                PointTrackingMode.Positive,
                PositionAffinity.Predecessor,
                TextBuffer
            ).Value;

            // point is between the primary prompt (including) and the last character of the corresponding language/stdin buffer:
            if (point <= projectedInputBufferEnd) {
                var projectedLanguageBufferStart = TextView.BufferGraph.MapUpToBuffer(
                    new SnapshotPoint(inputSnapshot, 0),
                    PointTrackingMode.Positive,
                    PositionAffinity.Successor,
                    TextBuffer
                ).Value;

                var promptProjectionSpan = _projectionSpans[_promptLineMapping[closestPrecedingPrimaryPromptIndex].Value];
                if (point < projectedLanguageBufferStart - promptProjectionSpan.Length) {
                    // cursor is before the first language buffer:
                    return new SnapshotSpan(new SnapshotPoint(TextBuffer.CurrentSnapshot, 0), projectedLanguageBufferStart - promptProjectionSpan.Length);
                }

                // cursor is within the language buffer:
                return new SnapshotSpan(projectedLanguageBufferStart, projectedInputBufferEnd);
            }

            // this was the last primary/stdin prompt - select the part of the projection buffer behind the end of the language/stdin buffer:
            if (closestPrecedingPrimaryPromptIndex + 1 == _promptLineMapping.Count) {
                return new SnapshotSpan(
                    projectedInputBufferEnd,
                    new SnapshotPoint(TextBuffer.CurrentSnapshot, TextBuffer.CurrentSnapshot.Length)
                );
            }

            ReplSpan lastSpanBeforeNextPrompt = _projectionSpans[_promptLineMapping[closestPrecedingPrimaryPromptIndex + 1].Value - 1];
            Debug.Assert(lastSpanBeforeNextPrompt.Kind == ReplSpanKind.Output);

            // select all text in between the language buffer and the next prompt:
            var trackingSpan = lastSpanBeforeNextPrompt.TrackingSpan;
            return new SnapshotSpan(
                projectedInputBufferEnd,
                TextView.BufferGraph.MapUpToBuffer(
                    trackingSpan.GetEndPoint(trackingSpan.TextBuffer.CurrentSnapshot),
                    PointTrackingMode.Positive,
                    PositionAffinity.Predecessor,
                    TextBuffer
                ).Value
            );
        }
        
        /// <summary>
        /// Pastes from the clipboard into the text view
        /// </summary>
        public bool PasteClipboard() {
            return UIThread(() => {
                string format = _evaluator.FormatClipboard();
                if (format != null) {
                    InsertCode(format);
                } else if (Clipboard.ContainsText()) {
                    InsertCode(Clipboard.GetText());
                } else {
                    return false;
                }
                return true;
            });
        }

        /// <summary>
        /// Indents the line where the caret is currently located.
        /// </summary>
        /// <remarks>
        /// We don't send this command to the editor since smart indentation doesn't work along with BufferChanged event.
        /// Instead, we need to implement indentation ourselves. We still use ISmartIndentProvider provided by the languge.
        /// </remarks>
        private void IndentCurrentLine() {
            Debug.Assert(_currentLanguageBuffer != null);

            var langCaret = TextView.BufferGraph.MapDownToBuffer(
                Caret.Position.BufferPosition,
                PointTrackingMode.Positive,
                _currentLanguageBuffer,
                PositionAffinity.Successor);

            if (langCaret == null) {
                return;
            }

            ITextSnapshotLine langLine = langCaret.Value.GetContainingLine();
            int? langIndentation = _languageIndenter.GetDesiredIndentation(langLine);

            if (langIndentation != null) {
                if (langCaret.Value == langLine.End) {
                    // create virtual space:
                    TextView.Caret.MoveTo(new VirtualSnapshotPoint(Caret.Position.BufferPosition, langIndentation.Value));
                } else {
                    // insert whitespace indentation:
                    string whitespace = GetWhiteSpaceForVirtualSpace(langIndentation.Value);
                    _currentLanguageBuffer.Insert(langCaret.Value, whitespace);
                }
            }
        }

        // Mimics EditorOperations.GetWhiteSpaceForPositionAndVirtualSpace.
        private string GetWhiteSpaceForVirtualSpace(int virtualSpaces) {
            string textToInsert;
            if (!TextView.Options.IsConvertTabsToSpacesEnabled()) {
                int tabSize = TextView.Options.GetTabSize();

                int spacesAfterPreviousTabStop = virtualSpaces % tabSize;
                int columnOfPreviousTabStop = virtualSpaces - spacesAfterPreviousTabStop;

                int requiredTabs = (columnOfPreviousTabStop + tabSize - 1) / tabSize;

                if (requiredTabs > 0) {
                    textToInsert = new string('\t', requiredTabs) + new string(' ', spacesAfterPreviousTabStop);
                } else {
                    textToInsert = new string(' ', virtualSpaces);
                }
            } else {
                textToInsert = new string(' ', virtualSpaces);
            }

            return textToInsert;
        }

        private int IndexOfLastStandardInputSpan()
        {
            for (int i = _projectionSpans.Count - 1; i >= 0; i--)
            {
                if (_projectionSpans[i].Kind == ReplSpanKind.StandardInput)
                {
                    return i;
                }
            }
            return -1;
        }

        /// <summary>
        /// Add a zero-width tracking span at the end of the projection buffer mapping to the end of the standard input buffer.
        /// </summary>
        private void AddStandardInputSpan()
        {
            ReplSpan promptSpan = CreateStandardInputPrompt();

            var stdInputSpan = new CustomTrackingSpan(
                _stdInputBuffer.CurrentSnapshot,
                new Span(_stdInputBuffer.CurrentSnapshot.Length, 0),
                PointTrackingMode.Negative,
                PointTrackingMode.Positive
            );

            ReplSpan inputSpan = new ReplSpan(stdInputSpan, ReplSpanKind.StandardInput);

            AppendProjectionSpans(promptSpan, inputSpan);
        }

        /// <summary>
        /// Deletes characters preceeding the current caret position in the current language buffer.
        /// </summary>
        private void DeletePreviousCharacter() {
            SnapshotPoint? point = MapToEditableBuffer(TextView.Caret.Position.BufferPosition);

            // We are not in an editable buffer, or we are at the start of the buffer, nothing to delete.
            if (point == null || point.Value == 0) {
                return;
            }

            var line = point.Value.GetContainingLine();
            int characterSize;
            if (line.Start.Position == point.Value.Position) {
                Debug.Assert(line.LineNumber != 0);
                characterSize = line.Snapshot.GetLineFromLineNumber(line.LineNumber - 1).LineBreakLength;
            } else {
                characterSize = 1;
            }

            point.Value.Snapshot.TextBuffer.Delete(new Span(point.Value.Position - characterSize, characterSize));
        }

        /// <summary>
        /// Deletes currently selected text from the language buffer and optionally saves it to the clipboard.
        /// </summary>
        private void CutOrDeleteSelection(bool isCut) {
            Debug.Assert(_currentLanguageBuffer != null);

            StringBuilder deletedText = null;

            // split into multiple deletes that only affect the language/input buffer:
            ITextBuffer affectedBuffer = (_stdInputStart != null) ? _stdInputBuffer : _currentLanguageBuffer;
            using (var edit = affectedBuffer.CreateEdit()) {
                foreach (var projectionSpan in TextView.Selection.SelectedSpans) {
                    var spans = TextView.BufferGraph.MapDownToBuffer(projectionSpan, SpanTrackingMode.EdgeInclusive, affectedBuffer);
                    foreach (var span in spans) {
                        if (isCut) {
                            if (deletedText == null) {
                                deletedText = new StringBuilder();
                            }
                            deletedText.Append(span.GetText());
                        }
                        edit.Delete(span);
                    }
                }
                edit.Apply();
            }

            // copy the deleted text to the clipboard:
            if (deletedText != null) {
                var data = new DataObject();
                if (TextView.Selection.Mode == TextSelectionMode.Box) {
                    data.SetData(_boxSelectionCutCopyTag, new object());
                }

                data.SetText(deletedText.ToString());
                Clipboard.SetDataObject(data, true);
            }

            // if the selection spans over prompts the prompts remain selected, so clear manually:
            TextView.Selection.Clear();
        }

        //public void ShowContextMenu() {
        //    var uishell = (IVsUIShell)WindowPane.GetService(typeof(SVsUIShell));
        //    if (uishell != null) {
        //        var pt = System.Windows.Forms.Cursor.Position;
        //        var pnts = new[] { new POINTS { x = (short)pt.X, y = (short)pt.Y } };
        //        var guid = Guids.guidReplWindowCmdSet;
        //        int hr = uishell.ShowContextMenu(
        //            0,
        //            ref guid,
        //            0x2100,
        //            pnts,
        //            TextView as IOleCommandTarget);

        //        ErrorHandler.ThrowOnFailure(hr);
        //    }
        //}

        #endregion

        #region Command Filters

        // LineBreak is sent as RETURN to language services. We set this flag to distinguish LineBreak from RETURN 
        // when we receive it back in post-language command filter.
        private bool ReturnIsLineBreak;

        private const uint CommandEnabled = (uint)(OLECMDF.OLECMDF_ENABLED | OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_DEFHIDEONCTXTMENU);
        private const uint CommandDisabled = (uint)(OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_DEFHIDEONCTXTMENU);
        private const uint CommandDisabledAndHidden = (uint)(OLECMDF.OLECMDF_INVISIBLE | OLECMDF.OLECMDF_SUPPORTED | OLECMDF.OLECMDF_DEFHIDEONCTXTMENU);

        private enum CommandFilterLayer {
            PreLanguage,
            PostLanguage,
            PreEditor
        }

        private sealed class CommandFilter : IOleCommandTarget {
#pragma warning disable 0649
            private readonly NodejsInteractiveWindow _replWindow;
            private readonly CommandFilterLayer _layer;
#pragma warning restore 0649

            public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) {
                switch (_layer) {
                    case CommandFilterLayer.PreLanguage:
                        return _replWindow.PreLanguageCommandFilterQueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

                    case CommandFilterLayer.PostLanguage:
                        return _replWindow.PostLanguageCommandFilterQueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);

                    case CommandFilterLayer.PreEditor:
                        return _replWindow.PreEditorCommandFilterQueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
                }

                throw new InvalidOperationException();
            }

            public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
                switch (_layer) {
                    case CommandFilterLayer.PreLanguage:
                        return _replWindow.PreLanguageCommandFilterExec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

                    case CommandFilterLayer.PostLanguage:
                        return _replWindow.PostLanguageCommandFilterExec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);

                    case CommandFilterLayer.PreEditor:
                        return _replWindow.PreEditorCommandFilterExec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
                }

                throw new InvalidOperationException();
            }
        }

        #region Pre-langauge service IOleCommandTarget

        private int PreLanguageCommandFilterQueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) {
            var nextTarget = _languageServiceCommandFilter;

            if (pguidCmdGroup == Microsoft.NodejsTools.Repl.Guids.guidReplWindowCmdSet) {
                switch (prgCmds[0].cmdID) {
                    case PkgCmdIDList.cmdidBreakLine:
                        prgCmds[0].cmdf = CommandEnabled;
                        return VSConstants.S_OK;
                }
            }

            return nextTarget.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        private int PreLanguageCommandFilterExec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
            var nextTarget = _languageServiceCommandFilter;

            if (pguidCmdGroup == VSConstants.VSStd2K) {
                switch ((VSConstants.VSStd2KCmdID)nCmdID) {
                    case VSConstants.VSStd2KCmdID.RETURN:
                        _historySearch = null;
                        if (_stdInputStart != null) {
                            if (InStandardInputRegion(TextView.Caret.Position.BufferPosition)) {
                                SubmitStandardInput();
                            }
                            return VSConstants.S_OK;
                        }
                        
                        ReturnIsLineBreak = false;
                        break;

                    //case VSConstants.VSStd2KCmdID.SHOWCONTEXTMENU:
                    //    ShowContextMenu();
                    //    return VSConstants.S_OK;

                    case VSConstants.VSStd2KCmdID.TYPECHAR:
                        _historySearch = null;
                        char typedChar = (char)(ushort)Marshal.GetObjectForNativeVariant(pvaIn);
                        if (!(_stdInputStart != null ? CaretInStandardInputRegion : CaretInActiveCodeRegion)) {
                            MoveCaretToCurrentInputEnd();
                        }

                        if (!TextView.Selection.IsEmpty) {
                            // delete selected text first
                            CutOrDeleteSelection(false);
                        }
                        break;
                }
            } else if (pguidCmdGroup == Guids.guidReplWindowCmdSet) {
                switch (nCmdID) {
                    case PkgCmdIDList.cmdidBreakLine:
                        // map to RETURN, so that IntelliSense and other services treat this as a new line
                        Guid group = VSConstants.VSStd2K;
                        ReturnIsLineBreak = true;
                        return nextTarget.Exec(ref group, (int)VSConstants.VSStd2KCmdID.RETURN, 0, IntPtr.Zero, IntPtr.Zero);
                }
            }

            return nextTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        /// <summary>
        /// Moves the caret to the end of the current input.  Called when the user types
        /// outside of an input line.
        /// </summary>
        private void MoveCaretToCurrentInputEnd() {
            // TODO (tomat): this is strange - we should rather find the next writable span
            EditorOperations.MoveToEndOfDocument(false);
        }

        #endregion

        internal IEnumerable<KeyValuePair<ReplSpanKind, SnapshotPoint>> GetOverlappingPrompts(SnapshotSpan span)
        {
            if (_promptLineMapping == null || _promptLineMapping.Count == 0 || _projectionSpans.Count == 0)
            {
                yield break;
            }

            var currentSnapshotSpan = span.TranslateTo(CurrentSnapshot, SpanTrackingMode.EdgeInclusive);
            var startLine = currentSnapshotSpan.Start.GetContainingLine();
            var endLine = currentSnapshotSpan.End.GetContainingLine();

            var promptMappingIndex = GetPromptMappingIndex(startLine.LineNumber);

            do
            {
                int lineNumber = _promptLineMapping[promptMappingIndex].Key;
                int promptIndex = _promptLineMapping[promptMappingIndex].Value;

                // no overlapping prompts will be found beyond the last line of the span:
                if (lineNumber > endLine.LineNumber)
                {
                    break;
                }

                // enumerate all prompts of the input block (primary and secondary):
                do
                {
                    var line = CurrentSnapshot.GetLineFromLineNumber(lineNumber);
                    ReplSpan projectionSpan = _projectionSpans[promptIndex];
                    Debug.Assert(projectionSpan.Kind.IsPrompt());

                    if (line.Start.Position >= currentSnapshotSpan.Span.Start || line.Start.Position < currentSnapshotSpan.Span.End)
                    {
                        yield return new KeyValuePair<ReplSpanKind, SnapshotPoint>(
                            projectionSpan.Kind,
                            new SnapshotPoint(CurrentSnapshot, line.Start)
                        );
                    }

                    promptIndex += SpansPerLineOfInput;
                    lineNumber++;
                } while (promptIndex < _projectionSpans.Count && _projectionSpans[promptIndex].Kind == ReplSpanKind.SecondaryPrompt);

                // next input block:
                promptMappingIndex++;
            } while (promptMappingIndex < _promptLineMapping.Count);
        }

        #region Post-language service IOleCommandTarget

        private int PostLanguageCommandFilterQueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) {
            return _editorServicesCommandFilter.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        private int PostLanguageCommandFilterExec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
            var nextTarget = _editorServicesCommandFilter;
            return nextTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        #endregion

        #region Pre-Editor service IOleCommandTarget

        private int PreEditorCommandFilterQueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText) {
            return _editorCommandFilter.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText);
        }

        private int PreEditorCommandFilterExec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut) {
            var nextTarget = _editorCommandFilter;

            if (pguidCmdGroup == VSConstants.VSStd2K) {
                switch ((VSConstants.VSStd2KCmdID)nCmdID) {
                    case VSConstants.VSStd2KCmdID.RETURN:
                        if (_currentLanguageBuffer == null) {
                            break;
                        }

                        // RETURN might be sent by LineBreak command:
                        bool trySubmit = !ReturnIsLineBreak;
                        ReturnIsLineBreak = false;

                        // RETURN that is not handled by any language or editor service is a "try submit" command

                        var langCaret = TextView.BufferGraph.MapDownToBuffer(
                            Caret.Position.BufferPosition,
                            PointTrackingMode.Positive,
                            _currentLanguageBuffer,
                            PositionAffinity.Successor
                        );

                        if (langCaret != null) {
                            if (trySubmit && CanExecuteActiveCode()) {
                                Submit();
                                return VSConstants.S_OK;
                            }

                            // insert new line (triggers secondary prompt injection in buffer changed event):
                            _currentLanguageBuffer.Insert(langCaret.Value.Position, GetLineBreak());
                            IndentCurrentLine();

                            return VSConstants.S_OK;
                        } else if (!CaretInStandardInputRegion) {
                            MoveCaretToCurrentInputEnd();
                            return VSConstants.S_OK;
                        }
                        break;

                    // TODO: 
                    //case VSConstants.VSStd2KCmdID.DELETEWORDLEFT:
                    //case VSConstants.VSStd2KCmdID.DELETEWORDRIGHT:
                    //    break;

                    case VSConstants.VSStd2KCmdID.BACKSPACE:
                        if (!TextView.Selection.IsEmpty) {
                            CutOrDeleteSelection(false);
                            return VSConstants.S_OK;
                        }

                        if (TextView.Caret.Position.VirtualSpaces == 0) {
                            DeletePreviousCharacter();
                            return VSConstants.S_OK;
                        }

                        break;

                    case VSConstants.VSStd2KCmdID.UP:
                        // UP at the end of input or with empty input rotate history:
                        if (_currentLanguageBuffer != null && !_isRunning && CaretAtEnd && _useSmartUpDown) {
                            HistoryPrevious();
                            return VSConstants.S_OK;
                        }

                        break;

                    case VSConstants.VSStd2KCmdID.DOWN:
                        // DOWN at the end of input or with empty input rotate history:
                        if (_currentLanguageBuffer != null && !_isRunning && CaretAtEnd && _useSmartUpDown) {
                            HistoryNext();
                            return VSConstants.S_OK;
                        }

                        break;

                    case VSConstants.VSStd2KCmdID.CANCEL:
                        _historySearch = null;
                        Cancel();
                        break;

                    case VSConstants.VSStd2KCmdID.BOL:
                        Home(false);
                        return VSConstants.S_OK;

                    case VSConstants.VSStd2KCmdID.BOL_EXT:
                        Home(true);
                        return VSConstants.S_OK;

                    case VSConstants.VSStd2KCmdID.EOL:
                        End(false);
                        return VSConstants.S_OK;

                    case VSConstants.VSStd2KCmdID.EOL_EXT:
                        End(true);
                        return VSConstants.S_OK;
                }
            } else if (pguidCmdGroup == VSConstants.GUID_VSStandardCommandSet97) {
                switch ((VSConstants.VSStd97CmdID)nCmdID) {
                    case VSConstants.VSStd97CmdID.Paste:
                        if (!(_stdInputStart != null ? CaretInStandardInputRegion : CaretInActiveCodeRegion)) {
                            MoveCaretToCurrentInputEnd();
                        }
                        PasteClipboard();
                        return VSConstants.S_OK;

                    case VSConstants.VSStd97CmdID.Cut:
                        if (!TextView.Selection.IsEmpty) {
                            CutOrDeleteSelection(true);
                            return VSConstants.S_OK;
                        }
                        break;

                    case VSConstants.VSStd97CmdID.Delete:
                        if (!TextView.Selection.IsEmpty) {
                            CutOrDeleteSelection(false);
                            return VSConstants.S_OK;
                        }
                        break;

                    case VSConstants.VSStd97CmdID.SelectAll:
                        SelectAll();
                        return VSConstants.S_OK;
                }
            }

            return nextTarget.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut);
        }

        #endregion
        
        #endregion

        #region Caret and Cursor

        private ITextCaret Caret {
            get { return TextView.Caret; }
        }

        private bool CaretAtEnd {
            get { return Caret.Position.BufferPosition.Position == CurrentSnapshot.Length; }
        }

        public bool CaretInActiveCodeRegion {
            get {
                if (_currentLanguageBuffer == null) {
                    return false;
                }

                var point = TextView.BufferGraph.MapDownToBuffer(
                    Caret.Position.BufferPosition,
                    PointTrackingMode.Positive,
                    _currentLanguageBuffer,
                    PositionAffinity.Successor
                );

                return point != null;
            }
        }

        public bool CaretInStandardInputRegion {
            get {
                if (_stdInputBuffer == null) {
                    return false;
                }

                var point = TextView.BufferGraph.MapDownToBuffer(
                    Caret.Position.BufferPosition,
                    PointTrackingMode.Positive,
                    _stdInputBuffer,
                    PositionAffinity.Successor
                );

                return point != null;
            }
        }

        /// <summary>
        /// Maps point to the current language buffer or standard input buffer.
        /// </summary>
        private SnapshotPoint? MapToEditableBuffer(SnapshotPoint projectionPoint) {
            SnapshotPoint? result = null;

            if (_currentLanguageBuffer != null) {
                result = TextView.BufferGraph.MapDownToBuffer(
                    projectionPoint, PointTrackingMode.Positive, _currentLanguageBuffer, PositionAffinity.Successor
                );
            }

            if (result != null) {
                return result;
            }

            if (_stdInputBuffer != null) {
                result = TextView.BufferGraph.MapDownToBuffer(
                    projectionPoint, PointTrackingMode.Positive, _stdInputBuffer, PositionAffinity.Successor
                );
            }

            return result;
        }

        private ITextSnapshot GetCodeSnapshot(SnapshotPoint projectionPosition) {
            var pt = TextView.BufferGraph.MapDownToFirstMatch(
                projectionPosition,
                PointTrackingMode.Positive,
                x => x.TextBuffer.ContentType == _languageContentType,
                PositionAffinity.Successor
            );

            return pt != null ? pt.Value.Snapshot : null;
        }

        /// <summary>
        /// Returns the insertion point relative to the current language buffer.
        /// </summary>
        private int GetActiveCodeInsertionPosition() {
            Debug.Assert(_currentLanguageBuffer != null);

            var langPoint = _textViewHost.TextView.BufferGraph.MapDownToBuffer(
                new SnapshotPoint(
                    _projectionBuffer.CurrentSnapshot,
                    Caret.Position.BufferPosition.Position
                ),
                PointTrackingMode.Positive,
                _currentLanguageBuffer,
                PositionAffinity.Predecessor
            );

            if (langPoint != null) {
                return langPoint.Value;
            }

            return _currentLanguageBuffer.CurrentSnapshot.Length;
        }

        private void ResetCursor() {
            if (_executionTimer != null) {
                _executionTimer.Stop();
            }
            if (_oldCursor != null) {
                ((ContentControl)TextView).Cursor = _oldCursor;
            }
            /*if (_oldCaretBrush != null) {
                CurrentView.Caret.RegularBrush = _oldCaretBrush;
            }*/

            _oldCursor = null;
            //_oldCaretBrush = null;
            _executionTimer = null;
        }

        private void StartCursorTimer() {
            // Save the old value of the caret brush so it can be restored
            // after execution has finished
            //_oldCaretBrush = CurrentView.Caret.RegularBrush;

            // Set the caret's brush to transparent so it isn't shown blinking
            // while code is executing in the REPL
            //CurrentView.Caret.RegularBrush = Brushes.Transparent;

            var timer = new DispatcherTimer();
            timer.Tick += SetRunningCursor;
            timer.Interval = new TimeSpan(0, 0, 0, 0, 250);
            _executionTimer = timer;
            timer.Start();
        }

        private void SetRunningCursor(object sender, EventArgs e) {
            var view = (ContentControl)TextView;

            // Save the old value of the cursor so it can be restored
            // after execution has finished
            _oldCursor = view.Cursor;

            // TODO: Design work to come up with the correct cursor to use
            // Set the repl's cursor to the "executing" cursor
            view.Cursor = Cursors.Wait;

            // Stop the timer so it doesn't fire again
            if (_executionTimer != null) {
                _executionTimer.Stop();
            }
        }

        #endregion

        #region Active Code and Standard Input

        /// <summary>
        /// Returns the full text of the current active input.
        /// </summary>
        private string GetActiveCode() {
            return _currentLanguageBuffer.CurrentSnapshot.GetText();
        }
        
        /// <summary>
        /// Sets the active code to the specified text w/o executing it.
        /// </summary>
        private void SetActiveCode(string text) {
            _currentLanguageBuffer.Replace(new Span(0, _currentLanguageBuffer.CurrentSnapshot.Length), text);
        }

        /// <summary>
        /// Appends given text to the last input span (standard input or active code input).
        /// </summary>
        private void AppendInput(string text) {
            Debug.Assert(CheckAccess());

            var inputSpan = _projectionSpans[_projectionSpans.Count - 1];
            Debug.Assert(inputSpan.Kind == ReplSpanKind.Language || inputSpan.Kind == ReplSpanKind.StandardInput);
            Debug.Assert(inputSpan.TrackingSpan.TrackingMode == SpanTrackingMode.Custom);
                
            var buffer = inputSpan.TrackingSpan.TextBuffer;
            var span = inputSpan.TrackingSpan.GetSpan(buffer.CurrentSnapshot);
            using (var edit = buffer.CreateEdit()) {
                edit.Insert(edit.Snapshot.Length, text);
                edit.Apply();
            }

            var replSpan = new ReplSpan(
                new CustomTrackingSpan(
                    buffer.CurrentSnapshot,
                    new Span(span.Start, span.Length + text.Length),
                    PointTrackingMode.Negative, 
                    PointTrackingMode.Positive
                ),
                inputSpan.Kind
            );

            ReplaceProjectionSpan(_projectionSpans.Count - 1, replSpan);

            Caret.EnsureVisible();
        }

        private void ClearInput() {
            Debug.Assert(_projectionSpans.Count > 0);

            // Finds the last primary prompt (standard input or code input).
            // Removes all spans following the primary prompt from the projection buffer.
            int i = _projectionSpans.Count - 1;
            while (i >= 0) {
                if (_projectionSpans[i].Kind == ReplSpanKind.Prompt || _projectionSpans[i].Kind == ReplSpanKind.StandardInputPrompt) {
                    Debug.Assert(i != _projectionSpans.Count - 1);
                    break;
                } 
                i--;
            }

            if (_projectionSpans[i].Kind != ReplSpanKind.StandardInputPrompt) {
                _currentLanguageBuffer.Delete(new Span(0, _currentLanguageBuffer.CurrentSnapshot.Length));
            } else {
                Debug.Assert(_stdInputStart != null);
                _stdInputBuffer.Delete(Span.FromBounds(_stdInputStart.Value, _stdInputBuffer.CurrentSnapshot.Length));
            }
        }
        
        private void PrepareForInput() {
            _buffer.Flush();
            _buffer.ResetColors();

            AddLanguageBuffer();
            
            _isRunning = false;
            ResetCursor();

            // we are prepared for processing any postponed submissions there might have been:
            ProcessPendingSubmissions();
        }

        private void ProcessPendingSubmissions() {
            Debug.Assert(_currentLanguageBuffer != null);

            if (_pendingSubmissions == null || _pendingSubmissions.Count == 0) {
                RestoreUncommittedInput();

                // move to the end (it migth have been in virtual space):
                Caret.MoveTo(GetLastLine().End);
                Caret.EnsureVisible();

                var ready = ReadyForInput;
                if (ready != null) {
                    ready();
                }

                return;
            }

            string submission = _pendingSubmissions.Dequeue();

            // queue new work item:
            Dispatcher.Invoke(new Action(() => {
                SetActiveCode(submission);
                Submit();
            }));
        }

        public void Submit() {
            RequiresLanguageBuffer();
            AppendLineNoPromptInjection(_currentLanguageBuffer);
            ApplyProtection(_currentLanguageBuffer, regions: null);
            ExecuteActiveCode();
        }

        private void StoreUncommittedInput() {
            if (_uncommittedInput == null) {
                string activeCode = GetActiveCode();
                if (!String.IsNullOrEmpty(activeCode)) {
                    _uncommittedInput = activeCode;
                }
            }
        }

        private void AppendUncommittedInput(string text) {
            if (String.IsNullOrEmpty(_uncommittedInput)) {
                _uncommittedInput = text;
            } else {
                _uncommittedInput += text;
            }
        }

        private void RestoreUncommittedInput() {
            if (_uncommittedInput != null) {
                SetActiveCode(_uncommittedInput);
                _uncommittedInput = null;
            }
        }

        private bool InStandardInputRegion(SnapshotPoint point) {
            if (_stdInputStart == null) {
                return false;
            }

            var stdInputPoint = TextView.BufferGraph.MapDownToBuffer(
                point,
                PointTrackingMode.Positive,
                _stdInputBuffer,
                PositionAffinity.Successor
            );

            return stdInputPoint != null && stdInputPoint.Value.Position >= _stdInputStart.Value;
        }

        private void CancelStandardInput() {
            AppendLineNoPromptInjection(_stdInputBuffer);
            _inputValue = null;
            _inputEvent.Set();
        }

        private void SubmitStandardInput() {
            AppendLineNoPromptInjection(_stdInputBuffer);
            _inputValue = _stdInputBuffer.CurrentSnapshot.GetText(_stdInputStart.Value, _stdInputBuffer.CurrentSnapshot.Length - _stdInputStart.Value);
            _inputEvent.Set();
        }

        #endregion

        #region Output

        public void WriteLine(string text)
        {
            _buffer.Write(text + GetLineBreak());
        }

        public void WriteOutput(object output) {
            UIThread(() => {
                Write(output);
            });
        }

        private void Write(object text, bool error = false) {
            if (_showOutput && !TryShowObject(text)) {
                // buffer the text
                if (text != null) {
                    _buffer.Write(text.ToString(), error);
                }
            }
        }

        private bool TryShowObject(object obj) {
            UIElement element = obj as UIElement;
            if (element != null) {
                _buffer.Flush();

                // figure out where we're inserting the image
                SnapshotPoint targetPoint = new SnapshotPoint(
                    TextView.TextBuffer.CurrentSnapshot,
                    TextView.TextBuffer.CurrentSnapshot.Length
                );

                for (int i = _projectionSpans.Count - 1; i >= 0; i--) {
                    if (_projectionSpans[i].Kind == ReplSpanKind.Output ||
                        (_projectionSpans[i].Kind == ReplSpanKind.Language && _isRunning)) {
                        // we've had some output during the execution and we hit that buffer.
                        // OR we hit a language input buffer, and we're running, and no output
                        // has been produced yet.

                        // In either case, this is where the image goes.
                        break;
                    }

                    // adjust where we're going to insert based upon the length of the span
                    targetPoint -= _projectionSpans[i].Length;

                    if (_projectionSpans[i].Kind == ReplSpanKind.Prompt) {
                        // we just walked past the primary input prompt, we want to put the
                        // image right before it.
                        break;
                    }
                }

                InlineReplAdornmentProvider.AddInlineAdornment(TextView, element, OnAdornmentLoaded, targetPoint);
                OnInlineAdornmentAdded();
                WriteLine(String.Empty);
                WriteLine(String.Empty);
                return true;
            }

            return false;
        }

        private void OnAdornmentLoaded(object source, EventArgs e) {
            ((ZoomableInlineAdornment)source).Loaded -= OnAdornmentLoaded;
            // Make sure the caret line is rendered
            DoEvents();
            Caret.EnsureVisible();
        }
        
        private void OnInlineAdornmentAdded() {
            _adornmentToMinimize = true;
        }

        #endregion

        #region Execution

        private bool CanExecuteActiveCode() {
            Debug.Assert(_currentLanguageBuffer != null);

            var input = GetActiveCode();
            if (input.Trim().Length == 0) {
                // Always allow "execution" of a blank line.
                // This will just close the current prompt and start a new one
                return true;
            }

            // Ignore any whitespace past the insertion point when determining
            // whether or not we're at the end of the input
            var pt = GetActiveCodeInsertionPosition();
            var atEnd = (pt == input.Length) || (pt >= 0 && input.Substring(pt).Trim().Length == 0);
            if (!atEnd) {
                return false;
            }

            // A command is never multi-line, so always try to execute something which looks like a command
            if (input.StartsWith(_commandPrefix)) {
                return true;
            }

            return Evaluator.CanExecuteCode(input);
        }

        /// <summary>
        /// Execute and then call the callback function with the result text.
        /// </summary>
        /// <param name="processResult"></param>
        private void ExecuteActiveCode() {
            UIThread(() => {
                // Ensure that the REPL doesn't try to execute if it is already
                // executing.  If this invariant can no longer be maintained more of
                // the code in this method will need to be bullet-proofed
                if (_isRunning) {
                    return;
                }

                var text = GetActiveCode();

                if (_adornmentToMinimize) {
                    InlineReplAdornmentProvider.MinimizeLastInlineAdornment(TextView);
                    _adornmentToMinimize = false;
                }
                
                TextView.Selection.Clear();

                _history.UncommittedInput = null;
                if (text.Trim().Length == 0) {
                    PrepareForInput();
                } else {
                    _history.Add(text.TrimEnd(_whitespaceChars));

                    _isRunning = true;

                    // Following method assumes that _isRunning will be cleared before 
                    // the following method is called again.
                    StartCursorTimer();

                    _sw = Stopwatch.StartNew();

                    Task<VisualStudio.InteractiveWindow.ExecutionResult> task = Evaluator.ExecuteCodeAsync(text) ?? VisualStudio.InteractiveWindow.ExecutionResult.Failed;
                    
                    task.ContinueWith(FinishExecute, _uiScheduler);
                }
            });
        }

        private void FinishExecute(Task<VisualStudio.InteractiveWindow.ExecutionResult> result) {
            Debug.Assert(CheckAccess());

            _sw.Stop();
            _buffer.Flush();

            if (_history.Last != null) {
                _history.Last.Duration = _sw.Elapsed.Seconds;
            }

            if (result.IsCanceled || result.Exception != null || !result.Result.IsSuccessful) {
                if (_history.Last != null) {
                    _history.Last.Failed = true;
                }

                if (_pendingSubmissions != null && _pendingSubmissions.Count > 0) {
                    // there was an error with the last execution, clear the
                    // input queue due to the error.
                    _pendingSubmissions.Clear();
                }
            }
            _addedLineBreakOnLastOutput = false;
            PrepareForInput();
        }

        //TODO(avitalb) implement roslyn commands
        //private Task<ExecutionResult> ExecuteCommand(string text, bool updateHistory) {
        //    if (!text.StartsWith(_commandPrefix)) {
        //        return null;
        //    }

        //    string commandLine = text.Substring(_commandPrefix.Length).Trim();
        //    string command = commandLine.Split(' ')[0];
        //    string args = commandLine.Substring(command.Length).Trim();

        //    // TODO: no special casing, these should all be commands

        //    if (command == _commandPrefix) {
        //        // REPL-level comment; do nothing
        //        return ExecutionResult.Succeeded;
        //    }

        //    if (commandLine.Length == 0 || command == "help") {
        //        ShowReplHelp();
        //        return ExecutionResult.Succeeded;
        //    }

        //    IReplCommand commandHandler = _commands.Find(x => x.Command == command);
        //    if (commandHandler == null) {
        //        commandHandler = _commands.OfType<IReplCommand2>().FirstOrDefault(x => x.Aliases.Contains(command));
        //        if (commandHandler == null) {
        //            return null;
        //        }
        //    }

        //    if (updateHistory) {
        //        _history.Last.Command = true;
        //    }

        //    try {
        //        return commandHandler.Execute(this, args) ?? ExecutionResult.Failed;
        //    } catch (Exception e) {
        //        WriteError(String.Format("Command '{0}' failed: {1}", command, e.Message));
        //        return ExecutionResult.Failed;
        //    }
        //}

        private void ShowReplHelp() {
            var cmdnames = new List<IReplCommand>(_commands.Where(x => x.Command != null));
            cmdnames.Sort((x, y) => String.Compare(x.Command, y.Command));

            const string helpFmt = "  {0,-24}  {1}";
            WriteLine(string.Format(helpFmt, _commandPrefix + "help", "Show a list of REPL commands"));

            foreach (var cmd in cmdnames) {
                WriteLine(string.Format(helpFmt, GetCommandNameWithAliases(cmd), cmd.Description));
            }
        }

        private string GetCommandNameWithAliases(IReplCommand cmd) {
            var cmd2 = cmd as IReplCommand2;
            if (cmd2 != null && cmd2.Aliases != null) {
                string aliases = string.Join(",", cmd2.Aliases.Select(x => _commandPrefix + x));
                if (aliases.Length > 0) {
                    return string.Join(",", _commandPrefix + cmd.Command, aliases);
                }
            }

            return _commandPrefix + cmd.Command;
        }

        #endregion
        
        public PropertyCollection Properties {
            get { return _properties; }
        }


        #region Scopes

        internal void SetCurrentScope(string newItem) {
            string activeCode = GetActiveCode();
            ((IMultipleScopeEvaluator)_evaluator).SetScope(newItem);
            SetActiveCode(activeCode);
        }

        private void UpdateScopeList(object sender, EventArgs e) {
            if (!CheckAccess()) {
                Dispatcher.BeginInvoke(new Action(() => UpdateScopeList(sender, e)));
                return;
            }

            _currentScopes = ((IMultipleScopeEvaluator)_evaluator).GetAvailableScopes().ToArray();
        }

        private bool IsMultiScopeEnabled() {
            var multiScope = Evaluator as IMultipleScopeEvaluator;
            return multiScope != null && multiScope.EnableMultipleScopes;
        }

        private void MultipleScopeSupportChanged(object sender, EventArgs e) {
            _scopeListVisible = IsMultiScopeEnabled();
        }

        #endregion

        #region Buffers, Spans and Prompts

        private ReplSpan CreateStandardInputPrompt() {
            return CreatePrompt(_stdInputPrompt, ReplSpanKind.StandardInputPrompt);
        }

        private ReplSpan CreatePrimaryPrompt() {
            var result = CreatePrompt(_prompt, ReplSpanKind.Prompt);
            _currentInputId++;
            return result;
        }

        private ReplSpan CreatePrompt(string prompt, ReplSpanKind promptKind) {
            Debug.Assert(promptKind == ReplSpanKind.Prompt || promptKind == ReplSpanKind.StandardInputPrompt);

            var lastLine = GetLastLine();
            _promptLineMapping.Add(new KeyValuePair<int, int>(lastLine.LineNumber, _projectionSpans.Count));

            prompt = _displayPromptInMargin ? String.Empty : FormatPrompt(prompt, _currentInputId);
            return new ReplSpan(prompt, promptKind);
        }

        private ReplSpan CreateSecondaryPrompt() {
            string secondPrompt = _displayPromptInMargin ? String.Empty : FormatPrompt(_secondPrompt, _currentInputId - 1);
            return new ReplSpan(secondPrompt, ReplSpanKind.SecondaryPrompt);
        }

        private string FormatPrompt(string prompt, int currentInput) {
            if (!_formattedPrompts) {
                return prompt;
            }

            StringBuilder res = null;
            for (int i = 0; i < prompt.Length; i++) {
                if (prompt[i] == '\\' && i < prompt.Length - 1) {
                    if (res == null) {
                        res = new StringBuilder(prompt, 0, i, prompt.Length);
                    }
                    switch (prompt[++i]) {
                        case '\\': res.Append('\\'); break;
                        case '#': res.Append(currentInput.ToString()); break;
                        case 'D': res.Append(DateTime.Today.ToString()); break;
                        case 'T': res.Append(DateTime.Now.ToString()); break;
                        default:
                            res.Append('\\');
                            res.Append(prompt[i + 1]);
                            break;
                    }
                    
                } else if (res != null) {
                    res.Append(prompt[i]);
                }
            }

            if (res != null) {
                return res.ToString();
            }
            return prompt;
        }

        /// <summary>
        /// Binary search for a prompt located on given line number. If there is no such span returns the closest preceding span.
        /// </summary>
        internal int GetPromptMappingIndex(int lineNumber) {
            int start = 0;
            int end = _promptLineMapping.Count - 1;
            while (true) {
                Debug.Assert(start <= end);

                int mid = start + ((end - start) >> 1);
                int key = _promptLineMapping[mid].Key;

                if (lineNumber == key) {
                    return mid;
                }

                if (mid == start) {
                    Debug.Assert(start == end || start == end - 1);
                    return (lineNumber >= _promptLineMapping[end].Key) ? end : mid;
                }

                if (lineNumber > key) {
                    start = mid;
                } else {
                    end = mid;
                }
            }
        }

        /// <summary>
        /// Creates and adds a new language buffer to the projection buffer.
        /// </summary>
        private void AddLanguageBuffer() {
            var buffer = _bufferFactory.CreateTextBuffer(_languageContentType);
            buffer.Properties.AddProperty(typeof(IInteractiveEvaluator), _evaluator);

            // get the real classifier, and have our classifier start listening and forwarding events            
            var contentClassifier = _classifierAgg.GetClassifier(buffer);
            _primaryClassifier.AddClassifier(_projectionBuffer, buffer, contentClassifier);

            var previousBuffer = _currentLanguageBuffer;
            _currentLanguageBuffer = buffer;

            //_evaluator.ActiveLanguageBufferChanged(buffer, previousBuffer);

            // add the whole buffer to the projection buffer and set it up to expand to the right as text is appended
            ReplSpan promptSpan = CreatePrimaryPrompt();
            ReplSpan languageSpan = new ReplSpan(CreateLanguageTrackingSpan(new Span(0, 0)), ReplSpanKind.Language);

            // projection buffer update must be the last operation as it might trigger event that accesses prompt line mapping:
            AppendProjectionSpans(promptSpan, languageSpan);
        }

        /// <summary>
        /// Creates the language span for the last line of the active input.  This span
        /// is effectively edge inclusive so it will grow as the user types at the end.
        /// </summary>
        private ITrackingSpan CreateLanguageTrackingSpan(Span span) {
            return new CustomTrackingSpan(
                _currentLanguageBuffer.CurrentSnapshot,
                span, 
                PointTrackingMode.Negative, 
                PointTrackingMode.Positive);
        }

        /// <summary>
        /// Creates the tracking span for a line previous in the input.  This span
        /// is negative tracking on the end so when the user types at the beginning of
        /// the next line we don't grow with the change.
        /// </summary>
        /// <param name="span"></param>
        /// <returns></returns>
        private ITrackingSpan CreateOldLanguageTrackingSpan(Span span) {
            return new CustomTrackingSpan(
                _currentLanguageBuffer.CurrentSnapshot,
                span,
                PointTrackingMode.Negative,
                PointTrackingMode.Negative
            );
        }

        /// <summary>
        /// Marks the entire buffer as readonly.
        /// </summary>
        private static void ApplyProtection(ITextBuffer buffer, IReadOnlyRegion[] regions, bool allowAppend = false) {
            using (var readonlyEdit = buffer.CreateReadOnlyRegionEdit()) {
                int end = buffer.CurrentSnapshot.Length;
                Span span = new Span(0, end);

                var region0 = allowAppend ?
                    readonlyEdit.CreateReadOnlyRegion(span, SpanTrackingMode.EdgeExclusive, EdgeInsertionMode.Allow) :
                    readonlyEdit.CreateReadOnlyRegion(span, SpanTrackingMode.EdgeExclusive, EdgeInsertionMode.Deny);

                // Create a second read-only region to prevent insert at start of buffer.
                var region1 = (end > 0) ? readonlyEdit.CreateReadOnlyRegion(new Span(0, 0), SpanTrackingMode.EdgeExclusive, EdgeInsertionMode.Deny) : null;

                readonlyEdit.Apply();

                if (regions != null) {
                    regions[0] = region0;
                    regions[1] = region1;
                }
            }
        }

        /// <summary>
        /// Removes readonly region from buffer.
        /// </summary>
        private static void RemoveProtection(ITextBuffer buffer, IReadOnlyRegion[] regions) {
            if (regions[0] != null) {
                Debug.Assert(regions[1] != null);

                foreach (var region in regions) {
                    using (var readonlyEdit = buffer.CreateReadOnlyRegionEdit()) {
                        readonlyEdit.RemoveReadOnlyRegion(region);
                        readonlyEdit.Apply();
                    }
                }
            }
        }

        private const int SpansPerLineOfInput = 2;
        private static object SuppressPromptInjectionTag = new object();

        private struct SpanRangeEdit
        {
            public int Start;
            public int Count;
            public ReplSpan[] Replacement;

            public SpanRangeEdit(int start, int count, ReplSpan[] replacement)
            {
                Start = start;
                Count = count;
                Replacement = replacement;
            }
        }

        private void AppendLineNoPromptInjection(ITextBuffer buffer) {
            using (var edit = buffer.CreateEdit(EditOptions.None, null, SuppressPromptInjectionTag)) {
                edit.Insert(buffer.CurrentSnapshot.Length, GetLineBreak());
                edit.Apply();
            }
        }
        
        // 
        // WARNING: When updating projection spans we need to update _projectionSpans list first and 
        // then projection buffer, since the projection buffer update might trigger events that might 
        // access the projection spans.
        //

        private void ClearProjection() {
            int count = _projectionSpans.Count;
            _projectionSpans.Clear();
            _projectionBuffer.DeleteSpans(0, count);
            InitializeProjectionBuffer();
        }

        private void InitializeProjectionBuffer() {
            // we need at least one non-inert span due to bugs in projection buffer, so insert an empty output span:
            var trackingSpan = new CustomTrackingSpan(
                _outputBuffer.CurrentSnapshot,
                new Span(0, 0),
                PointTrackingMode.Negative,
                PointTrackingMode.Negative
            );

            AppendProjectionSpan(new ReplSpan(trackingSpan, ReplSpanKind.Output));
        }

        private void AppendProjectionSpan(ReplSpan span) {
            InsertProjectionSpan(_projectionSpans.Count, span);
        }

        private void AppendProjectionSpans(ReplSpan span1, ReplSpan span2) {
            InsertProjectionSpans(_projectionSpans.Count, span1, span2);
        }

        private void InsertProjectionSpan(int index, ReplSpan span) {
            _projectionSpans.Insert(index, span);
            _projectionBuffer.ReplaceSpans(index, 0, new[] { span.Span }, EditOptions.None, null);
        }

        private void InsertProjectionSpans(int index, ReplSpan span1, ReplSpan span2) {
            _projectionSpans.Insert(index, span1);
            _projectionSpans.Insert(index + 1, span2);
            _projectionBuffer.ReplaceSpans(index, 0, new[] { span1.Span, span2.Span }, EditOptions.None, null);
        }

        private void ReplaceProjectionSpan(int spanToReplace, ReplSpan newSpan) {
            _projectionSpans[spanToReplace] = newSpan;
            _projectionBuffer.ReplaceSpans(spanToReplace, 1, new[] { newSpan.Span }, EditOptions.None, null);
        }

        #endregion

        /// <summary>
        /// Appends text to the output buffer and updates projection buffer to include it.
        /// </summary>
        internal void AppendOutput(OutputBuffer.OutputEntry[] entries)
        {
            if (!entries.Any())
            {
                return;
            }

            int oldBufferLength = _outputBuffer.CurrentSnapshot.Length;
            int oldLineCount = _outputBuffer.CurrentSnapshot.LineCount;

            RemoveProtection(_outputBuffer, _outputProtection);

            var spans = new List<KeyValuePair<Span, OutputBuffer.OutputEntryProperties>>(entries.Length);

            // append the text to output buffer and make sure it ends with a line break:
            using (var edit = _outputBuffer.CreateEdit())
            {
                if (_addedLineBreakOnLastOutput)
                {
                    // appending additional output, remove the line break we previously injected
                    var lineBreak = GetLineBreak();
                    var deleteSpan = new Span(_outputBuffer.CurrentSnapshot.Length - lineBreak.Length, lineBreak.Length);
                    Debug.Assert(_outputBuffer.CurrentSnapshot.GetText(deleteSpan) == lineBreak);
                    edit.Delete(deleteSpan);
                    oldBufferLength -= lineBreak.Length;
                    _addedLineBreakOnLastOutput = false;
                }

                var text = String.Empty;
                var startPosition = oldBufferLength;
                foreach (var entry in entries)
                {
                    text = entry.Buffer.ToString();
                    edit.Insert(oldBufferLength, text);

                    var span = new Span(startPosition, text.Length);
                    spans.Add(new KeyValuePair<Span, OutputBuffer.OutputEntryProperties>(span, entry.Properties));
                    startPosition += text.Length;
                }
                if (!_readingStdIn && !EndsWithLineBreak(text))
                {
                    var lineBreak = GetLineBreak();
                    edit.Insert(oldBufferLength, lineBreak);
                    _addedLineBreakOnLastOutput = true;
                    // Adust last span to include line break
                    var last = spans.Last();
                    var newLastSpan = new Span(last.Key.Start, last.Key.Length + lineBreak.Length);
                    spans[spans.Count() - 1] = new KeyValuePair<Span, OutputBuffer.OutputEntryProperties>(newLastSpan, last.Value);
                }
                edit.Apply();
            }

            ApplyProtection(_outputBuffer, _outputProtection);

            int newLineCount = _outputBuffer.CurrentSnapshot.LineCount;
            int insertBeforePrompt = -1;
            if (!_isRunning)
            {
                int lastPrimaryPrompt, lastPrompt;

                IndexOfLastPrompt(out lastPrimaryPrompt, out lastPrompt);

                // If the last prompt is STDIN prompt insert output before it, otherwise before the primary prompt:
                insertBeforePrompt = (lastPrompt != -1 && _projectionSpans[lastPrompt].Kind == ReplSpanKind.StandardInputPrompt) ? lastPrompt : lastPrimaryPrompt;
            }

            foreach (var entry in spans)
            {
                var span = entry.Key;
                var props = entry.Value;

                var trackingSpan = new CustomTrackingSpan(
                    _outputBuffer.CurrentSnapshot,
                    span,
                    PointTrackingMode.Negative,
                    PointTrackingMode.Negative);

                var outputSpan = new ReplSpan(trackingSpan, ReplSpanKind.Output);
                _outputColors.Add(new ColoredSpan(span, props.Color));

                // insert output span immediately before the last primary span
                if (insertBeforePrompt >= 0)
                {
                    if (oldLineCount != newLineCount)
                    {
                        int delta = newLineCount - oldLineCount;
                        Debug.Assert(delta > 0);

                        // update line -> projection span index mapping for the last primary prompt
                        var lastMaplet = _promptLineMapping.Last();
                        _promptLineMapping[_promptLineMapping.Count - 1] = new KeyValuePair<int, int>(
                            lastMaplet.Key + delta,
                            lastMaplet.Value + 1);
                    }

                    // Projection buffer change might trigger events that access prompt line mapping, so do it last:
                    InsertProjectionSpan(insertBeforePrompt, outputSpan);
                }
                else
                {
                    AppendProjectionSpan(outputSpan);
                }
            }
        }

        private static bool EndsWithLineBreak(string str)
        {
            return str.Length > 0 && (str[str.Length - 1] == '\n' || str[str.Length - 1] == '\r');
        }

        private void IndexOfLastPrompt(out int lastPrimary, out int last)
        {
            last = -1;
            lastPrimary = -1;
            for (int i = _projectionSpans.Count - 1; i >= 0; i--)
            {
                switch (_projectionSpans[i].Kind)
                {
                    case ReplSpanKind.Prompt:
                        lastPrimary = i;
                        if (last == -1)
                        {
                            last = i;
                        }
                        return;

                    case ReplSpanKind.SecondaryPrompt:
                    case ReplSpanKind.StandardInputPrompt:
                        if (last == -1)
                        {
                            last = i;
                        }
                        break;
                }
            }
        }
        #region Editor Helpers

        private ITextSnapshotLine GetLastLine() {
            return GetLastLine(CurrentSnapshot);
        }

        private static ITextSnapshotLine GetLastLine(ITextSnapshot snapshot) {
            return snapshot.GetLineFromLineNumber(snapshot.LineCount - 1);
        }
        private static ITextSnapshotLine GetPreviousLine(ITextSnapshotLine line) {
            return line.LineNumber > 0 ? line.Snapshot.GetLineFromLineNumber(line.LineNumber - 1) : null;
        }

        private string GetLineBreak() {
            return _textViewHost.TextView.Options.GetNewLineCharacter();
        }

        private static int IndexOfNonWhiteSpaceCharacter(ITextSnapshotLine line) {
            var snapshot = line.Snapshot;
            int start = line.Start.Position;
            int count = line.Length;
            for (int i = 0; i < count; i++) {
                if (!Char.IsWhiteSpace(snapshot[start + i])) {
                    return i;
                }
            }
            return -1;
        }

        private static string TrimTrailingEmptyLines(ITextSnapshot snapshot) {
            var line = GetLastLine(snapshot);
            while (line != null && line.Length == 0) {
                line = GetPreviousLine(line);
            }

            if (line == null) {
                return String.Empty;
            }

            return line.Snapshot.GetText(0, line.ExtentIncludingLineBreak.End.Position);
        }

        #endregion

        #region IVsFindTarget Members

        public int Find(string pszSearch, uint grfOptions, int fResetStartPoint, IVsFindHelper pHelper, out uint pResult) {
            if (_findTarget != null) {
                return _findTarget.Find(pszSearch, grfOptions, fResetStartPoint, pHelper, out pResult);
            }
            pResult = 0;
            return VSConstants.E_NOTIMPL;
        }

        public int GetCapabilities(bool[] pfImage, uint[] pgrfOptions) {
            if (_findTarget != null && pgrfOptions != null && pgrfOptions.Length > 0 && _projectionSpans.Count > 0) {
                return _findTarget.GetCapabilities(pfImage, pgrfOptions);
            }
            return VSConstants.E_NOTIMPL;
        }

        public int GetCurrentSpan(TextSpan[] pts) {
            if (_findTarget != null) {
                return _findTarget.GetCurrentSpan(pts);
            }
            return VSConstants.E_NOTIMPL;
        }

        public int GetFindState(out object ppunk) {
            if (_findTarget != null) {
                return _findTarget.GetFindState(out ppunk);
            }
            ppunk = null;
            return VSConstants.E_NOTIMPL;

        }

        public int GetMatchRect(RECT[] prc) {
            if (_findTarget != null) {
                return _findTarget.GetMatchRect(prc);
            }
            return VSConstants.E_NOTIMPL;
        }

        public int GetProperty(uint propid, out object pvar) {
            if (_findTarget != null) {
                return _findTarget.GetProperty(propid, out pvar);
            }
            pvar = null;
            return VSConstants.E_NOTIMPL;
        }

        public int GetSearchImage(uint grfOptions, IVsTextSpanSet[] ppSpans, out IVsTextImage ppTextImage) {
            if (_findTarget != null) {
                return _findTarget.GetSearchImage(grfOptions, ppSpans, out ppTextImage);
            }
            ppTextImage = null;
            return VSConstants.E_NOTIMPL;
        }

        public int MarkSpan(TextSpan[] pts) {
            if (_findTarget != null) {
                return _findTarget.MarkSpan(pts);
            }
            return VSConstants.E_NOTIMPL;
        }

        public int NavigateTo(TextSpan[] pts) {
            if (_findTarget != null) {
                return _findTarget.NavigateTo(pts);
            }
            return VSConstants.E_NOTIMPL;
        }

        public int NotifyFindTarget(uint notification) {
            if (_findTarget != null) {
                return _findTarget.NotifyFindTarget(notification);
            }
            return VSConstants.E_NOTIMPL;
        }

        public int Replace(string pszSearch, string pszReplace, uint grfOptions, int fResetStartPoint, IVsFindHelper pHelper, out int pfReplaced) {
            if (_findTarget != null) {
                return _findTarget.Replace(pszSearch, pszReplace, grfOptions, fResetStartPoint, pHelper, out pfReplaced);
            }
            pfReplaced = 0;
            return VSConstants.E_NOTIMPL;
        }

        public int SetFindState(object pUnk) {
            if (_findTarget != null) {
                return _findTarget.SetFindState(pUnk);
            }
            return VSConstants.E_NOTIMPL;
        }

        #endregion

        #region UI Dispatcher Helpers

        private Dispatcher Dispatcher {
            get { return ((FrameworkElement)TextView).Dispatcher; }
        }

        private bool CheckAccess() {
            return Dispatcher.CheckAccess();
        }

        private T UIThread<T>(Func<T> func) {
            if (!CheckAccess()) {
                return (T)Dispatcher.Invoke(func);
            }
            return func();
        }

        private void RemoveLastInputPrompt()
        {
            var prompt = _projectionSpans[_projectionSpans.Count - SpansPerLineOfInput];
            Debug.Assert(prompt.Kind.IsPrompt());
            if (prompt.Kind == ReplSpanKind.Prompt || prompt.Kind == ReplSpanKind.StandardInputPrompt)
            {
                _promptLineMapping.RemoveAt(_promptLineMapping.Count - 1);
            }

            // projection buffer update must be the last operation as it might trigger event that accesses prompt line mapping:
            RemoveProjectionSpans(_projectionSpans.Count - SpansPerLineOfInput, SpansPerLineOfInput);
        }

        private void RemoveProjectionSpans(int index, int count)
        {
            _projectionSpans.RemoveRange(index, count);
            _projectionBuffer.DeleteSpans(index, count);
        }
   
        private void UIThread(Action action) {
            if (!CheckAccess()) {
                try {
                    Dispatcher.Invoke(action);
                } catch (OperationCanceledException) {
                    // VS is shutting down
                }
                return;
            }
            action();
        }

        private static void DoEvents() {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action<DispatcherFrame>(f => f.Continue = false),
                frame
                );
            Dispatcher.PushFrame(frame);
        }

        public Task<VisualStudio.InteractiveWindow.ExecutionResult> InitializeAsync()
        {
            throw new NotImplementedException();
            //reference IInteractiveWindow.InitializeAsync() instead? that would be the equivalent of what ReplWindow did
        }

        public void Close()
        {
            throw new NotImplementedException();
            //doesn't have equivalent in ReplWindow
        }

        public System.Threading.Tasks.Task SubmitAsync(IEnumerable<string> inputs)
        {
            if (!CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(() => Submit(inputs)));
            }

            if (_stdInputStart == null)
            {
                if (!_isRunning && _currentLanguageBuffer != null)
                {
                    StoreUncommittedInput();
                    PendSubmissions(inputs);
                    ProcessPendingSubmissions();
                }
                else
                {
                    PendSubmissions(inputs);
                }
            }
            return System.Threading.Tasks.Task.CompletedTask;
        }

        Span IInteractiveWindow.WriteLine(string text)
        {
            /*int result =*/_buffer.Write(text);
            _buffer.Write(text + GetLineBreak());
            return new Span();// result, (text != null ? text.Length : 0) + _lineBreakString.Length);
        }

        public Span Write(string text, bool error = false)
        {
            if (_showOutput && !TryShowObject(text))
            {
                // buffer the text
                if (text != null)
                {
                    _buffer.Write(text.ToString(), error);
                }
            }
            //need to return a Span
            return new Span();
        }

        public Span WriteErrorLine(string text)
        {
            //what old replwindow did
            UIThread(() => {
                Write(text, error: true);
            });
            //TODO(avitalb) need to add a line break
            return new Span();
        }

        public Span WriteError(string text)
        {
            UIThread(() => {
                Write(text, error: true);
            });
            return new Span();//text != null ? text.Length : 0
        }

        public void Write(UIElement element)
        {
            if (_showOutput && !TryShowObject(element))
            {
                // buffer the text
                if (element != null)
                {
                    _buffer.Write(element.ToString());
                }
            }
        }

        public void FlushOutput()
        {
        //    // if we're rapidly outputting grow the output buffer.
        //    long curTime = _sw.ElapsedMilliseconds;
        //    if (curTime - _lastFlush < 1000)
        //    {
        //        if (_maxSize < 1024 * 1024)
        //        {
        //            _maxSize *= 2;
        //        }
        //    }
        //    _lastFlush = _sw.ElapsedMilliseconds;

        //    OutputBuffer.OutputEntry[] entries;
        //    lock (_lock)
        //    {
        //        entries = _outputEntries.ToArray();

        //        _outputEntries.Clear();
        //        _bufferLength = 0;
        //        _timer.IsEnabled = false;
        //    }

        //    if (entries.Length > 0)
        //    {
        //        _window.AppendOutput(entries);
        //        _window.TextView.Caret.EnsureVisible();
        //    }
        }

        TextReader IInteractiveWindow.ReadStandardInput()
        {
            // shouldn't be called on the UI thread because we'll hang
            Debug.Assert(!CheckAccess());

            bool wasRunning = _isRunning;
            _readingStdIn = true;
            UIThread(() => {
                _buffer.Flush();

                if (_isRunning)
                {
                    _isRunning = false;
                }
                else if (_projectionSpans.Count > 0 && _projectionSpans[_projectionSpans.Count - 1].Kind == ReplSpanKind.Language)
                {
                    // we need to remove our input prompt.
                    RemoveLastInputPrompt();
                }

                AddStandardInputSpan();

                Caret.EnsureVisible();
                ResetCursor();

                _isRunning = false;
                _uncommittedInput = null;
                _stdInputStart = _stdInputBuffer.CurrentSnapshot.Length;
            });

            var ready = ReadyForInput;
            if (ready != null)
            {
                ready();
            }

            _inputEvent.WaitOne();
            _stdInputStart = null;
            _readingStdIn = false;

            UIThread(() => {
                // if the user cleared the screen we cancelled the input, so we won't have our span here.
                // We can also have an interleaving output span, so we'll search back for the last input span.
                int i = IndexOfLastStandardInputSpan();
                if (i != -1)
                {
                    RemoveProtection(_stdInputBuffer, _stdInputProtection);

                    // replace previous span w/ a span that won't grow...
                    var newSpan = new ReplSpan(
                        new CustomTrackingSpan(
                            _stdInputBuffer.CurrentSnapshot,
                            _projectionSpans[i].TrackingSpan.GetSpan(_stdInputBuffer.CurrentSnapshot),
                            PointTrackingMode.Negative,
                            PointTrackingMode.Negative
                        ),
                        ReplSpanKind.StandardInput
                    );

                    ReplaceProjectionSpan(i, newSpan);
                    ApplyProtection(_stdInputBuffer, _stdInputProtection, allowAppend: true);

                    if (wasRunning)
                    {
                        _isRunning = true;
                    }
                    else
                    {
                        PrepareForInput();
                    }
                }
            });

            // input has been cancelled:
            if (_inputValue != null)
            {
                _history.Add(_inputValue);
            }

            return new StringReader(_inputValue);
        }

        public void AddInput(string input)
        {
            throw new NotImplementedException();
        }

        public void Dispose()
        {
            throw new NotImplementedException();
        }

        public Span Write(string text)
        {
            throw new NotImplementedException();
        }

        #endregion

        #region Testing

        internal List<ReplSpan> ProjectionSpans {
            get { return _projectionSpans; }
        }

        public ITextBuffer OutputBuffer
        {
            get
            {
                return _outputBuffer;
            }
        }

        public TextWriter OutputWriter
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public TextWriter ErrorOutputWriter
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public bool IsRunning
        {
            get
            {
                return _isRunning;
            }
        }

        public bool IsResetting
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public bool IsInitializing
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public IInteractiveWindowOperations Operations
        {
            get
            {
                throw new NotImplementedException();
            }
        }

        public ITextBuffer CurrentLanguageBuffer
        {
            get
            {
                return _currentLanguageBuffer;
            }
        }

        #endregion

    }
}
