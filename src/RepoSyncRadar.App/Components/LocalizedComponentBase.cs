using Microsoft.AspNetCore.Components;

namespace RepoSyncRadar.App.Components;

public abstract class LocalizedComponentBase : ComponentBase
{
    public const string DisplayCultureCascadeName = "DisplayCulture";

    [CascadingParameter(Name = DisplayCultureCascadeName)]
    public string DisplayCulture { get; set; } = AppDisplayCulture.DefaultCultureName;

    protected void ApplyDisplayCultureForRender()
        => AppDisplayCulture.Apply(DisplayCulture);
}
