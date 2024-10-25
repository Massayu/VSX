using Microsoft.VisualStudio.ComponentModelHost;
using Microsoft;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Operations;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using Task = System.Threading.Tasks.Task;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Utilities;
using System.Collections.Generic;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows;
using System.ComponentModel.Composition;

namespace VSIXProject1;

[PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
[Guid(VSIXProject1Package.PackageGuidString)]
[ProvideAutoLoad(UIContextGuids.NoSolution, PackageAutoLoadFlags.BackgroundLoad)] // Ensure it loads when VS starts
public sealed class VSIXProject1Package : AsyncPackage
{
    public const string PackageGuidString = "2f7c4434-b1bc-4263-a7e7-ec0beda87ac7";

    protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
    {
        await this.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
        // Get the IComponentModel service to initialize MEF components
        var componentModel = (IComponentModel)await GetServiceAsync(typeof(SComponentModel));
        Assumes.Present(componentModel);

        var navigatorService = componentModel.GetService<ITextStructureNavigatorSelectorService>();
        var formatMapService = componentModel.GetService<IEditorFormatMapService>();
        Assumes.Present(navigatorService);
        Assumes.Present(formatMapService);
    }

    public class IconTag : IntraTextAdornmentTag
    {
        private readonly SnapshotPoint _position;

        public IconTag(SnapshotPoint position, Action<SnapshotPoint> clickCallback)
            : base(CreateIconElement(position, clickCallback), null)
        {
            _position = position;
        }

        private static UIElement CreateIconElement(SnapshotPoint position, Action<SnapshotPoint> clickCallback)
        {
            var icon = new TextBlock
            {
                Text = "🖰",
                FontSize = 18,
                Foreground = new SolidColorBrush(Color.FromRgb(0x3, 0x7F, 0xCB)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 4, 0),
                Cursor = Cursors.Hand
            };

            icon.MouseDown += (sender, e) =>
            {
                e.Handled = true;
                clickCallback?.Invoke(position);
            };

            return icon;
        }
    }

    internal class IconTagger : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly ITextBuffer _buffer;
        private readonly string _searchText = "if (auto";
        private bool _isDisposed;
        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public IconTagger(ITextBuffer buffer)
        {
            _buffer = buffer;
            _buffer.Changed += BufferChanged;
        }

        private void HandleIconClick(SnapshotPoint position)
        {
            if (_isDisposed) 
                return;
            var line = position.GetContainingLine();
            var lineNumber = line.LineNumber + 1;
            var column = position.Position - line.Start.Position + 1;
            MessageBox.Show($"Icon clicked at line {lineNumber}, column {column}");
        }

        private void BufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_isDisposed) 
                return;
            TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(new SnapshotSpan(_buffer.CurrentSnapshot, 0, _buffer.CurrentSnapshot.Length)));
        }

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (spans.Count == 0 || _isDisposed) 
                yield break;

            var snapshot    = spans[0].Snapshot;
            var entireText  = snapshot.GetText();
            int searchStart = 0;

            while ((searchStart = entireText.IndexOf(_searchText, searchStart, StringComparison.OrdinalIgnoreCase)) != -1)
            {
                var autoIndex   = searchStart + _searchText.IndexOf("auto", StringComparison.OrdinalIgnoreCase);
                var snapshotPos = new SnapshotPoint(snapshot, autoIndex);
                yield return new TagSpan<IntraTextAdornmentTag>(new SnapshotSpan(snapshotPos, 0), new IconTag(snapshotPos, HandleIconClick));
                searchStart += _searchText.Length;
            }
        }

        public void Dispose()
        {
            if (!_isDisposed)
            {
                _isDisposed = true;
                _buffer.Changed -= BufferChanged;
            }
        }
    }

    internal class IntraTextAdornmentManager
    {
        private readonly IWpfTextView _view;
        private readonly ITagAggregator<IntraTextAdornmentTag> _tagAggregator;
        private readonly IAdornmentLayer _layer;
        private readonly Dictionary<SnapshotSpan, UIElement> _activeAdornments;
        private bool _isUpdating;

        public IntraTextAdornmentManager(IWpfTextView view, ITagAggregator<IntraTextAdornmentTag> tagAggregator)
        {
            _view = view;
            _tagAggregator = tagAggregator;
            _layer = view.GetAdornmentLayer("IconAdornment");
            _activeAdornments = new Dictionary<SnapshotSpan, UIElement>();
            _tagAggregator.TagsChanged += OnTagsChanged;
            UpdateAdornments();
        }

        private void OnTagsChanged(object sender, TagsChangedEventArgs e)
        {
            var spans = e.Span.GetSpans(_view.TextSnapshot);
            foreach (var span in spans)
            {
                ClearAdornments(span);
            }
            UpdateAdornments();
        }

        private void UpdateAdornments()
        {
            if (_isUpdating) 
                return;
            _isUpdating = true;
            try
            {
                var snapshot = _view.TextSnapshot;
                var visibleSpan = _view.TextViewLines.FormattedSpan;
                var tags = _tagAggregator.GetTags(new NormalizedSnapshotSpanCollection(visibleSpan));

                foreach (var tagSpan in tags)
                {
                    var spans = tagSpan.Span.GetSpans(snapshot);
                    if (spans.Count == 0) 
                        continue;

                    var snapshotSpan = spans[0];
                    if (!snapshotSpan.Snapshot.Equals(snapshot)) 
                        continue;

                    if (_activeAdornments.ContainsKey(snapshotSpan)) 
                        continue;

                    var element = CloneElement(tagSpan.Tag.Adornment as UIElement);
                    if (element == null) 
                        continue;

                    var line = _view.TextViewLines.GetTextViewLineContainingBufferPosition(snapshotSpan.Start);
                    if (line == null || !line.Snapshot.Equals(snapshot)) 
                        continue;

                    var charBounds = line.GetCharacterBounds(snapshotSpan.Start);
                    Canvas.SetLeft(element, charBounds.Left);
                    Canvas.SetTop(element, charBounds.Top);

                    _layer.AddAdornment(AdornmentPositioningBehavior.TextRelative, snapshotSpan, null, element,
                        (tag, removed) => _activeAdornments.Remove(snapshotSpan));

                    _activeAdornments[snapshotSpan] = element;
                }
            }
            finally
            {
                _isUpdating = false;
            }
        }

        private void ClearAdornments(SnapshotSpan span)
        {
            if (_activeAdornments.TryGetValue(span, out var element))
            {
                _layer.RemoveAdornment(element);
                _activeAdornments.Remove(span);
            }
        }

        private static UIElement CloneElement(UIElement element)
        {
            if (element is TextBlock original)
            {
                return new TextBlock
                {
                    Text = original.Text,
                    FontSize = original.FontSize,
                    Foreground = original.Foreground,
                    VerticalAlignment = original.VerticalAlignment,
                    Margin = original.Margin,
                    Cursor = original.Cursor
                };
            }
            return new TextBlock
            {
                Text = "🔧",
                FontSize = 14,
                Foreground = Brushes.Red,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(2, 0, 2, 0),
                Cursor = Cursors.Hand
            };
        }
    }

    [Export(typeof(ITaggerProvider))]
    [ContentType("text")]
    [TagType(typeof(IntraTextAdornmentTag))]
    internal class IconTaggerProvider : ITaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextBuffer buffer) where T : ITag
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            return new IconTagger(buffer) as ITagger<T>;
        }
    }

    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    public class IconAdornmentFactory : IWpfTextViewCreationListener
    {
        [Export(typeof(AdornmentLayerDefinition))]
        [Name("IconAdornment")]
        [Order(After = PredefinedAdornmentLayers.Text)]
        public static AdornmentLayerDefinition EditorAdornmentLayer = null;

        [Import]
        internal IViewTagAggregatorFactoryService TagAggregatorFactory { get; set; }

        public void TextViewCreated(IWpfTextView textView)
        {
            var tagAggregator = TagAggregatorFactory.CreateTagAggregator<IntraTextAdornmentTag>(textView);
            textView.Properties.GetOrCreateSingletonProperty(() =>
                new IntraTextAdornmentManager(textView, tagAggregator));
        }
    }

}
