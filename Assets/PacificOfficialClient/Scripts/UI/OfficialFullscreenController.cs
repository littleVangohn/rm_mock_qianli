using UnityEngine;

/// <summary>
/// Hides the official full-screen match states — the artwork the real client shows
/// when a robot dies, a warning lands, a dart hits, the energy unit is assembled or
/// the match ends. The simulator has no live feed for these events, so they must not
/// appear as static mock match data.
/// </summary>
public class OfficialFullscreenController : MonoBehaviour
{
    public void Initialize(OfficialLayout sharedLayout)
    {
        foreach (string path in new[]
        {
            "GamePrepItem", "KickOffByServer", "DeathMask", "RevivingMask",
            "RuneTab", "BigRuneItem", "TechCore2026", "MissileHitMask",
            "YellowWarningMask", "ServerKillMask", "RobotDeathItem",
            "AnimationBlueWin", "AnimationRedWin", "AnimationDraw",
            "ValidateItem", "RMUTCResultPrefab",
        })
            sharedLayout.SetActive(path, false);
    }
}
