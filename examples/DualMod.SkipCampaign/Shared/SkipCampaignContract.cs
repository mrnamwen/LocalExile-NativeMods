namespace DualMod.SkipCampaign.Shared;

/// <summary>
/// Shared protocol constants, option keys, UI paths, and verified PoE 3.29 / LocalExile defaults
/// for the <c>DualMod.SkipCampaign</c> pre-game showcase.
/// </summary>
public static class SkipCampaignProtocol
{
    /// <summary>Canonical lowercase dual-side mod identifier matching <c>mod.json</c>.</summary>
    public const string ModId = "skip-campaign";

    /// <summary>ServerBridge channel name used for server-to-client toast notices.</summary>
    public const string ChannelName = "skip-campaign";

    /// <summary>Structured <c>CreateCharacter.S78</c> option key (<c>{BaseState}|nm:skip_campaign=1</c>).</summary>
    public const string OptionKey = "skip_campaign";

    /// <summary>Option value indicating the player opted in to skip the campaign.</summary>
    public const string OptionEnabledValue = "1";

    /// <summary>Display caption rendered on both <c>LeagueCardModes</c> and <c>CreateCharacterBar</c> checkboxes.</summary>
    public const string CheckboxCaption = "Skip Campaign";

    /// <summary>Virtual path for the <c>ScreenSlot.CreateCharacterBar</c> mirror checkbox layout.</summary>
    public const string CreateCharacterBarUiPath = "Metadata/UI/Mods/skip-campaign/CreateCharacterSkipBar.ui";

    /// <summary>Slot attachment identifier inside <c>ScreenSlot.CreateCharacterBar</c>.</summary>
    public const string CreateCharacterBarSlotId = "skip_campaign_bar";

    /// <summary>CheckBox control identifier inside <see cref="CreateCharacterBarUiPath"/>.</summary>
    public const string CreateCharacterCheckBoxId = "skip_campaign_checkbox";

    /// <summary>Status label control identifier inside <see cref="CreateCharacterBarUiPath"/>.</summary>
    public const string CreateCharacterStatusLabelId = "skip_campaign_status";

    /// <summary>Persisted character-data key recording one-time skip-campaign finalization.</summary>
    public const string FinalizedCharacterDataKey = "skip-campaign:applied";

    /// <summary>Default starting town <c>WorldAreas</c> Id (Karui Shores).</summary>
    public const string DefaultStartingAreaId = "2_11_endgame_town";

    /// <summary>Default starting level for skipped characters.</summary>
    public const int DefaultStartingLevel = 68;

    /// <summary>Default minimum character level counted as league campaign completion.</summary>
    public const int DefaultCompletionMinLevel = 68;

    /// <summary>Default starting gold credited to the league stash on first login.</summary>
    public const long DefaultStartingGold = 50_000;

    /// <summary>Default passive refund points granted on first login.</summary>
    public const ushort DefaultRefundPoints = 10;

    /// <summary>
    /// Default PoE 3.29 / LocalExile campaign quest flags applied on first login, including Act 5 and Act 10
    /// Kitava resistance penalties (<c>-60%</c> total), <c>EndGameAvailable</c>, Pantheon access/god unlocks,
    /// and tutorial suppression.
    /// </summary>
    public static IReadOnlyList<string> DefaultKitavaPenaltyQuestFlags { get; } =
    [
        "a5q6-CompletedBoss",
        "SinOnKitavaDefeat",
        "EndGameAvailable",
        "a10q3-UsedRewardBook",
        "CanChangePantheon",
        "a6q1-KilledBoss",
        "a7q1-KilledBoss",
        "a8q23-KilledBosses",
        "CompletedAllTutorials",
    ];

    /// <summary>
    /// Default Act 1–10 towns plus Karui Shores (<c>2_11_endgame_town</c>) waypoints granted on first login.
    /// </summary>
    public static IReadOnlyList<string> DefaultCampaignWaypoints { get; } =
    [
        "1_1_town",
        "1_2_town",
        "1_3_town",
        "1_4_town",
        "1_5_town",
        "2_6_town",
        "2_7_town",
        "2_8_town",
        "2_9_town",
        "2_10_town",
        "2_11_endgame_town",
    ];

    /// <summary>
    /// Default campaign Labyrinth ascendancy tiers (<c>AscendancyRules.AwardKeys</c>) awarded when
    /// <see cref="SkipCampaignSettings.GrantAscendancyTrials"/> is enabled.
    /// </summary>
    public static IReadOnlyList<string> DefaultAscendancyAwardKeys { get; } =
    [
        "normal",
        "cruel",
        "merciless",
    ];

    /// <summary>Returns <c>true</c> when <paramref name="options"/> enables <see cref="OptionKey"/>.</summary>
    public static bool IsOptInEnabled(IReadOnlyDictionary<string, string>? options)
    {
        if (options is null)
        {
            return false;
        }

        if (!options.TryGetValue(OptionKey, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return false;
        }

        string trimmed = raw.Trim();
        return string.Equals(trimmed, OptionEnabledValue, StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(trimmed, "yes", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Configurable server settings governing opt-in campaign skipping, league completion eligibility,
/// starting town/level, quest flags, waypoints, refund points, ascendancy trials, and starting gold.
/// </summary>
public sealed record SkipCampaignSettings
{
    /// <summary>Master switch enabling or disabling campaign skip on character creation.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, requires at least one living character on the same account in the exact same league
    /// (respecting SSF and Hardcore death isolation) at or above <see cref="CompletionMinLevel"/> or in endgame.
    /// </summary>
    public bool RequireLeagueCompletion { get; init; } = true;

    /// <summary>Target <c>WorldAreas</c> town Id assigned before first area entry (default <c>2_11_endgame_town</c>).</summary>
    public string StartingAreaId { get; init; } = SkipCampaignProtocol.DefaultStartingAreaId;

    /// <summary>Starting character level (<c>1..100</c>) assigned before <c>S-&gt;C 0x15 / 0x13</c> (default <c>68</c>).</summary>
    public int StartingLevel { get; init; } = SkipCampaignProtocol.DefaultStartingLevel;

    /// <summary>Whether to award Labyrinth ascendancy trials on first in-game arrival (default <c>true</c>).</summary>
    public bool GrantAscendancyTrials { get; init; } = true;

    /// <summary>Starting gold credited to the player's league stash on first arrival (default <c>50000</c>).</summary>
    public long StartingGold { get; init; } = SkipCampaignProtocol.DefaultStartingGold;

    /// <summary>Passive refund points set on first arrival (default <c>10</c>).</summary>
    public ushort RefundPoints { get; init; } = SkipCampaignProtocol.DefaultRefundPoints;

    /// <summary>Minimum level of an existing living league character that satisfies <see cref="RequireLeagueCompletion"/>.</summary>
    public int CompletionMinLevel { get; init; } = SkipCampaignProtocol.DefaultCompletionMinLevel;

    /// <summary>Campaign/Kitava penalty quest flags applied on first arrival.</summary>
    public IReadOnlyList<string> KitavaPenaltyQuestFlags { get; init; } = SkipCampaignProtocol.DefaultKitavaPenaltyQuestFlags;

    /// <summary>Act 1–10 + Karui Shores waypoints granted on first arrival.</summary>
    public IReadOnlyList<string> CampaignWaypoints { get; init; } = SkipCampaignProtocol.DefaultCampaignWaypoints;

    /// <summary>Labyrinth ascendancy award keys granted when <see cref="GrantAscendancyTrials"/> is <c>true</c>.</summary>
    public IReadOnlyList<string> AscendancyAwardKeys { get; init; } = SkipCampaignProtocol.DefaultAscendancyAwardKeys;

    /// <summary>Default out-of-the-box configuration.</summary>
    public static SkipCampaignSettings Default { get; } = new();
}

/// <summary>
/// Server-to-client notification sent via <c>ServerBridge</c> on first in-game arrival after
/// campaign skip progression has been applied, triggering a HUD toast via <c>IToastService</c>.
/// </summary>
public sealed record SkipCampaignAppliedNotice(
    string CharacterName,
    string League,
    string StartingAreaId,
    int StartingLevel,
    int WaypointsGranted,
    int QuestFlagsApplied,
    ushort RefundPoints,
    int AscendancyPointsEarned,
    long GoldGranted,
    uint TotalGold);
