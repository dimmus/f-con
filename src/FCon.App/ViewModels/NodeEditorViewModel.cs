using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FCon.Abstractions.Model;
using FCon.Abstractions.Plugins;
using FCon.Core.Import;
using FCon.Core.Plugins;

namespace FCon.App.ViewModels;

/// <summary>
/// Editor for one server. Every field on screen — protocol credentials, transport, TLS,
/// mux — is rendered from a <see cref="FieldSpec"/>, so a newly installed plugin is fully
/// editable without a single line of UI code.
/// </summary>
public sealed partial class NodeEditorViewModel : ObservableObject
{
    private readonly PluginRegistry _registry;
    private readonly LinkImporter _importer;
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly Guid _id;
    private readonly Guid? _subscriptionId;

    [ObservableProperty] private string _remark = "";
    [ObservableProperty] private string _server = "";
    [ObservableProperty] private int _port = 443;
    [ObservableProperty] private string? _group;
    [ObservableProperty] private ProtocolDescriptor? _protocol;
    [ObservableProperty] private TransportKind _transport = TransportKind.Raw;
    [ObservableProperty] private SecurityKind _security = SecurityKind.None;
    [ObservableProperty] private ObservableCollection<FieldViewModel> _protocolFields = [];
    [ObservableProperty] private ObservableCollection<FieldViewModel> _transportFields = [];
    [ObservableProperty] private ObservableCollection<FieldViewModel> _securityFields = [];
    [ObservableProperty] private ObservableCollection<FieldViewModel> _muxFields = [];
    [ObservableProperty] private string _validationText = "";
    [ObservableProperty] private bool _hasValidationErrors;

    public NodeEditorViewModel(PluginRegistry registry, LinkImporter importer, ProxyNode node, bool isNew)
    {
        _registry = registry;
        _importer = importer;
        _id = node.Id;
        _subscriptionId = node.SubscriptionId;
        IsNew = isNew;

        Protocols = [.. registry.Plugins.Select(p => p.Descriptor)];
        LoadFrom(node);
    }

    public bool IsNew { get; }
    public string Title => IsNew ? "Add server" : "Edit server";
    public IReadOnlyList<ProtocolDescriptor> Protocols { get; }

    public IReadOnlyList<TransportKind> AvailableTransports =>
        Protocol?.Transports ?? [];

    public IReadOnlyList<SecurityKind> AvailableSecurity =>
        Protocol?.Security ?? [];

    public bool ShowTransport => AvailableTransports.Count > 0;
    public bool ShowSecurity => AvailableSecurity.Count > 0;
    public bool ShowMux => Protocol?.SupportsMux == true;

    /// <summary>Result of a successful save; null while the dialog is still open.</summary>
    public ProxyNode? Result { get; private set; }

    // -------------------------------------------------------------- loading

    private void LoadFrom(ProxyNode node)
    {
        Remark = node.Remark;
        Server = node.Server;
        Port = node.Port;
        Group = node.Group;

        _values.Clear();
        foreach (var (key, value) in node.Settings) _values[key] = value;
        NodeFormSchema.Read(node, _values);

        Protocol = Protocols.FirstOrDefault(p => p.Id == node.Protocol) ?? Protocols.FirstOrDefault();
        Transport = node.Transport.Kind;
        Security = node.Security.Kind;

        RebuildAll();
        Validate();
    }

    partial void OnProtocolChanged(ProtocolDescriptor? value)
    {
        if (value is null) return;

        // Keep the selection legal for the newly chosen protocol.
        if (value.Transports.Count > 0 && !value.Transports.Contains(Transport))
            Transport = value.Transports[0];
        if (value.Security.Count > 0 && !value.Security.Contains(Security))
            Security = value.Security[0];

        SeedDefaults(value.Fields);
        RebuildAll();
        OnPropertyChanged(nameof(AvailableTransports));
        OnPropertyChanged(nameof(AvailableSecurity));
        OnPropertyChanged(nameof(ShowTransport));
        OnPropertyChanged(nameof(ShowSecurity));
        OnPropertyChanged(nameof(ShowMux));
        Validate();
    }

    partial void OnTransportChanged(TransportKind value)
    {
        var specs = NodeFormSchema.ForTransport(value);
        SeedDefaults(specs);
        TransportFields = Build(specs);
        Validate();
    }

    partial void OnSecurityChanged(SecurityKind value)
    {
        var specs = NodeFormSchema.ForSecurity(value);
        SeedDefaults(specs);
        SecurityFields = Build(specs);
        Validate();
    }

    partial void OnServerChanged(string value) => Validate();

    partial void OnPortChanged(int value) => Validate();

    private void RebuildAll()
    {
        ProtocolFields = Build(Protocol?.Fields ?? []);
        TransportFields = Build(NodeFormSchema.ForTransport(Transport));
        SecurityFields = Build(NodeFormSchema.ForSecurity(Security));
        MuxFields = Build(NodeFormSchema.ForMux());
    }

    private ObservableCollection<FieldViewModel> Build(IReadOnlyList<FieldSpec> specs)
    {
        var models = specs
            .Select(spec => new FieldViewModel(spec, _values, OnFieldChanged))
            .ToList();

        // Wire visibility guards so dependent fields appear and vanish as the user types.
        foreach (var model in models)
        {
            if (model.Spec.VisibleWhenKey is null) continue;
            var driver = models.FirstOrDefault(m => m.Spec.Key == model.Spec.VisibleWhenKey);
            driver?.Dependents.Add(model);
            model.RefreshVisibility(_values);
        }

        return new ObservableCollection<FieldViewModel>(models);
    }

    /// <summary>Populate any value the user has never set from the spec default.</summary>
    private void SeedDefaults(IReadOnlyList<FieldSpec> specs)
    {
        foreach (var spec in specs)
        {
            if (spec.Default is not null && !_values.ContainsKey(spec.Key))
                _values[spec.Key] = spec.Default;
        }
    }

    private void OnFieldChanged(FieldViewModel field)
    {
        foreach (var dependent in field.Dependents) dependent.RefreshVisibility(_values);
        Validate();
    }

    // ------------------------------------------------------------ commands

    [RelayCommand]
    private void PasteLink(string? link)
    {
        if (string.IsNullOrWhiteSpace(link)) return;

        var result = _importer.Import(link);
        if (!result.AnySucceeded)
        {
            ValidationText = result.Errors.Count > 0 ? result.Errors[0] : "Link could not be parsed.";
            HasValidationErrors = true;
            return;
        }
        LoadFrom(result.Nodes[0] with { Id = _id, SubscriptionId = _subscriptionId });
    }

    [RelayCommand]
    private void GenerateUuid(FieldViewModel? field)
    {
        if (field is null) return;
        field.Value = Guid.NewGuid().ToString();
    }

    /// <summary>Validate and, if clean, publish the edited node into <see cref="Result"/>.</summary>
    public bool TrySave()
    {
        Validate();
        if (HasValidationErrors) return false;

        Result = BuildNode();
        return true;
    }

    public string? BuildShareLink()
    {
        var node = BuildNode();
        return _registry.ById(node.Protocol)?.BuildLink(node);
    }

    private ProxyNode BuildNode()
    {
        var descriptor = Protocol ?? Protocols[0];

        // Only persist the keys this protocol actually declares, so switching protocols
        // does not leave stale credentials from the previous one on the node.
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in descriptor.Fields)
        {
            if (_values.TryGetValue(spec.Key, out var value) && !string.IsNullOrEmpty(value))
                settings[spec.Key] = value;
        }

        return new ProxyNode
        {
            Id = _id,
            Protocol = descriptor.Id,
            Remark = Remark.Trim(),
            Server = Server.Trim(),
            Port = Port,
            Group = string.IsNullOrWhiteSpace(Group) ? null : Group.Trim(),
            Settings = settings,
            Transport = descriptor.Transports.Count == 0
                ? TransportOptions.Default
                : NodeFormSchema.WriteTransport(Transport, _values),
            Security = descriptor.Security.Count == 0
                ? SecurityOptions.None
                : NodeFormSchema.WriteSecurity(Security, _values),
            Mux = descriptor.SupportsMux ? NodeFormSchema.WriteMux(_values) : MuxOptions.Disabled,
            SubscriptionId = _subscriptionId,
        };
    }

    private void Validate()
    {
        if (Protocol is null) return;

        var issues = _importer.Validate(BuildNode());
        HasValidationErrors = issues.Count > 0;
        ValidationText = issues.Count == 0
            ? "Ready to save."
            : string.Join(Environment.NewLine, issues);
    }
}

/// <summary>One editable field, bound directly to the editor's shared value map.</summary>
public sealed partial class FieldViewModel : ObservableObject
{
    private readonly IDictionary<string, string> _values;
    private readonly Action<FieldViewModel> _onChanged;

    [ObservableProperty] private bool _isVisible = true;

    public FieldViewModel(FieldSpec spec, IDictionary<string, string> values, Action<FieldViewModel> onChanged)
    {
        Spec = spec;
        _values = values;
        _onChanged = onChanged;

        if (!values.ContainsKey(spec.Key) && spec.Default is not null)
            values[spec.Key] = spec.Default;
    }

    public FieldSpec Spec { get; }
    public List<FieldViewModel> Dependents { get; } = [];

    public string Label => Spec.Label;
    public string? Help => Spec.Help;
    public string? Placeholder => Spec.Placeholder;
    public bool IsRequired => Spec.Required;

    public FieldKind Kind => Spec.Kind;
    public bool IsText => Kind is FieldKind.Text or FieldKind.Number;
    public bool IsSecret => Kind == FieldKind.Secret;
    public bool IsToggle => Kind == FieldKind.Toggle;
    public bool IsChoice => Kind == FieldKind.Choice;
    public bool IsMultiline => Kind == FieldKind.Multiline;
    public bool IsUuid => Kind == FieldKind.Uuid;

    public IReadOnlyList<ChoiceOption> Choices => Spec.Choices;

    public string Value
    {
        get => _values.TryGetValue(Spec.Key, out var v) ? v : "";
        set
        {
            var normalised = value ?? "";
            if (Value == normalised) return;
            _values[Spec.Key] = normalised;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BoolValue));
            OnPropertyChanged(nameof(SelectedChoice));
            _onChanged(this);
        }
    }

    public bool BoolValue
    {
        get => Value.Equals("true", StringComparison.OrdinalIgnoreCase);
        set => Value = value ? "true" : "false";
    }

    public ChoiceOption? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => c.Value == Value) ?? Choices.FirstOrDefault();
        set => Value = value?.Value ?? "";
    }

    public void RefreshVisibility(IDictionary<string, string> values)
    {
        if (Spec.VisibleWhenKey is null) return;
        var current = values.TryGetValue(Spec.VisibleWhenKey, out var v) ? v : "";
        IsVisible = Spec.VisibleWhenValues.Count == 0
            ? !string.IsNullOrWhiteSpace(current)
            : Spec.VisibleWhenValues.Contains(current, StringComparer.OrdinalIgnoreCase);
    }
}
