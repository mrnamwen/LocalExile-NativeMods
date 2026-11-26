using DualMod.IdolsAndGearVault.Shared;
using LocalExile.Client.Sdk.Engine;
using LocalExile.Client.Sdk.Extend;

namespace DualMod.IdolsAndGearVault.Client;

/// <summary>
/// Custom <c>.ui</c> control kind (<c>"IdolVault.StatusBadge"</c>) registered into 3.29
/// <c>IUiControlRegistry</c> (<c>g_uiControlFactoryMap</c>) demonstrating full native
/// <c>OnInitialize</c> / <c>OnPropertyChanged</c> / <c>OnFrame</c> / <c>OnDestroy</c> lifecycle.
/// </summary>
public sealed class IdolVaultStatusBadgeControl : ICustomUiControl
{
    private IUiControl? _host;
    private readonly Action<IdolVaultStatusBadgeControl>? _onLifecycleChanged;

    public IdolVaultStatusBadgeControl()
        : this(null)
    {
    }

    public IdolVaultStatusBadgeControl(Action<IdolVaultStatusBadgeControl>? onLifecycleChanged)
    {
        _onLifecycleChanged = onLifecycleChanged;
    }

    public string ControlTypeName => IdolsVaultProtocol.CustomControlStatusBadge;
    public bool Initialized { get; private set; }
    public bool Destroyed { get; private set; }
    public int FrameCount { get; private set; }
    public string BadgeText { get; private set; } = "0 / 32 Cells";
    public string AccentColor { get; private set; } = "rgb(236, 202, 142)";
    public string? LastPropertyName { get; private set; }
    public string? LastPropertyValue { get; private set; }

    public void OnInitialize(IUiControl hostControl)
    {
        _host = hostControl;
        Initialized = true;
        Destroyed = false;
        hostControl.Text = BadgeText;
        hostControl.SetProperty("accent_colour", AccentColor);
        _onLifecycleChanged?.Invoke(this);
    }

    public void OnPropertyChanged(string propertyName, string value)
    {
        LastPropertyName = propertyName;
        LastPropertyValue = value;

        if (string.Equals(propertyName, "badge_text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(propertyName, "text", StringComparison.OrdinalIgnoreCase))
        {
            BadgeText = value;
            if (_host is not null)
            {
                _host.Text = value;
            }
        }
        else if (string.Equals(propertyName, "accent_colour", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(propertyName, "colour", StringComparison.OrdinalIgnoreCase))
        {
            AccentColor = value;
        }

        _onLifecycleChanged?.Invoke(this);
    }

    public void OnFrame(float deltaSeconds)
    {
        if (Destroyed || deltaSeconds <= 0f)
        {
            return;
        }

        FrameCount++;
    }

    public void OnDestroy()
    {
        Destroyed = true;
        _host = null;
        _onLifecycleChanged?.Invoke(this);
    }
}
