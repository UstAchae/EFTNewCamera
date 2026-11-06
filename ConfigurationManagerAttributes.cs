namespace EFTNewCamera;

// Read by name by BepInEx ConfigurationManager (the F12 window). Only the fields used here are declared.
internal sealed class ConfigurationManagerAttributes
{
    // Place within the section; higher comes first.
    public int? Order;
    public bool? Browsable;   // false: kept in the config file, not shown in F12
}
