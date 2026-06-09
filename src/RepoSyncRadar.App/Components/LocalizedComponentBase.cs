using Microsoft.AspNetCore.Components;

namespace RepoSyncRadar.App.Components;

public abstract class LocalizedComponentBase : ComponentBase
{
    public const string DisplayCultureCascadeName = "DisplayCulture";

    [CascadingParameter(Name = DisplayCultureCascadeName)]
    public string DisplayCulture { get; set; } = AppDisplayCulture.DefaultCultureName;

    public override Task SetParametersAsync(ParameterView parameters)
    {
        parameters.SetParameterProperties(this);
        ApplyDisplayCulture();
        return base.SetParametersAsync(ParameterView.Empty);
    }

    protected override bool ShouldRender()
    {
        ApplyDisplayCulture();
        return base.ShouldRender();
    }

    private void ApplyDisplayCulture()
        => AppDisplayCulture.Apply(DisplayCulture);
}
