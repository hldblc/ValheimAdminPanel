namespace AdminPanel
{
    // ==================== Extra glue (finished half-built features) ====================
    // Not a wave: this hosts the items that were already half-present in the codebase and only needed
    // completing — currently the Direct Message section, which gives the long-registered but never-called
    // AP_SrvMsg companion RPC a caller.
    public partial class AdminPanelPlugin
    {
        partial void FeaturesInitExtra()
        {
            DmInit();
            RegisterFeatureSection(new FeatureSection
            { Id = "DirectMessage", LocKey = "dm.chip", Draw = DrawDirectMessageSection, Enabled = DmSectionEnabled });
        }

        partial void FeaturesResetExtra() => DmReset();
    }
}
