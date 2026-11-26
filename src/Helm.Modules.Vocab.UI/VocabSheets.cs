using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Threading;
using Helm.Core.Ui;

namespace Helm.Modules.Vocab.Ui;

/// <summary>
/// The sheets that follow the view model over the Vocabulary page: Analysis (<see cref="VocabViewModel.IsAgentOpen"/>),
/// Add text (<see cref="VocabTextsViewModel.IsOpen"/>), editing the open word (its <see cref="VocabWordEditor.IsOpen"/>)
/// and asking for a new analysis (<see cref="VocabWordDetailViewModel.IsAskingRedo"/>). A sheet opens when its state
/// turns on and closes when it turns off; closed by a tap outside, Escape or Back, it turns the state off. The old
/// dialogs over the page (WPF) and the phone's own bottom sheets are these sheets now.
/// </summary>
public sealed class VocabSheets
{
    private readonly Control _anchor;
    private readonly VocabViewModel _vm;
    private readonly Slot[] _slots;
    private VocabWordDetailViewModel? _detail;
    private bool _attached;
    private bool _queued;

    public VocabSheets(Control anchor, VocabViewModel vm)
    {
        _anchor = anchor;
        _vm = vm;
        _slots =
        [
            new Slot(() => vm.IsAgentOpen, () => new VocabAnalysisSheet(vm), 560, () => vm.CloseAgentCommand.Execute(null)),
            new Slot(() => vm.Texts.IsOpen, () => new VocabTextsSheet(vm), 600, () => vm.Texts.CloseCommand.Execute(null),
                Focus: body => ((VocabTextsSheet)body).FocusFirst()),
            new Slot(() => vm.Detail?.Editor.IsOpen == true, () => new VocabEditSheet(vm.Detail!), 600, () => _detail?.Editor.CancelCommand.Execute(null),
                CanClose: body => ((VocabEditSheet)body).IsUnchanged, Focus: body => ((VocabEditSheet)body).FocusFirst()),
            new Slot(() => vm.Detail?.IsAskingRedo == true, () => new VocabRedoSheet(vm.Detail!), 440, () =>
            {
                if (_detail is not null) _detail.IsAskingRedo = false;
            }, Focus: body => ((VocabRedoSheet)body).FocusNote()),
        ];
    }

    /// <summary>The open sheet of each kind, for tests: Analysis, Add text, Edit, New analysis.</summary>
    public IReadOnlyList<SheetHandle?> Open => _slots.Select(s => s.Handle).ToList();

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _vm.PropertyChanged += OnChanged;
        _vm.Texts.PropertyChanged += OnChanged;
        Watch(_vm.Detail);
        Queue();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        _vm.PropertyChanged -= OnChanged;
        _vm.Texts.PropertyChanged -= OnChanged;
        Watch(null);
        foreach (var slot in _slots) slot.Close();
    }

    private void Watch(VocabWordDetailViewModel? detail)
    {
        if (_detail is not null)
        {
            _detail.PropertyChanged -= OnChanged;
            _detail.Editor.PropertyChanged -= OnChanged;
        }
        _detail = detail;
        if (_detail is not null)
        {
            _detail.PropertyChanged += OnChanged;
            _detail.Editor.PropertyChanged += OnChanged;
        }
    }

    private void OnChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(VocabViewModel.Detail) && sender == _vm) Watch(_vm.Detail);
        if (e.PropertyName is nameof(VocabViewModel.IsAgentOpen) or nameof(VocabTextsViewModel.IsOpen) or nameof(VocabViewModel.Detail)
            or nameof(VocabWordEditor.IsOpen) or nameof(VocabWordDetailViewModel.IsAskingRedo))
            Queue();
    }

    /// <summary>Once the change is done (opening one sheet often closes another first).</summary>
    private void Queue()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            Update();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Opens the sheets whose state is on, closes the others.</summary>
    public void Update()
    {
        if (!_attached) return;
        foreach (var slot in _slots)
        {
            if (!slot.Wanted() || TopLevel.GetTopLevel(_anchor) is null) slot.Close();
            else slot.Show(_anchor);
        }
    }

    private sealed record Slot(Func<bool> Wanted, Func<Control> Build, double Width, Action Dismissed, Func<Control, bool>? CanClose = null,
        Action<Control>? Focus = null)
    {
        public SheetHandle? Handle { get; private set; }

        public void Show(Control anchor)
        {
            if (Handle is { IsOpen: true }) return;
            var body = Build();
            var handle = Sheet.Show(anchor, body, Width, CanClose is null ? null : () => CanClose(body));
            if (handle is null) return;
            Handle = handle;
            // Closed by a tap outside, Escape or Back: the view model's state follows.
            handle.Closed += () =>
            {
                if (ReferenceEquals(Handle, handle)) Handle = null;
                if (Wanted()) Dismissed();
            };
            if (Focus is not null) Dispatcher.UIThread.Post(() => Focus(body), DispatcherPriority.Input);
        }

        public void Close()
        {
            var handle = Handle;
            Handle = null;
            handle?.Close();
        }
    }
}
