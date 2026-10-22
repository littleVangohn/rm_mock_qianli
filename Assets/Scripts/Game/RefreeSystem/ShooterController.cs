using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Unity.Netcode;
using TMPro;

public class ShooterController : NetworkBehaviour 
{
    // Only upload the shooter postion, velocity and ShooterID, all the other details like whether this shootaction is valid will be judged by refereecontroller
    // This is called trigger action is because the player only pulls the trigger, whether there will be a bullet is determined by other factors...
    public delegate void TriggerAction(int shooterID, Vector3 userPosition, Vector3 shootVelocity);
    public event TriggerAction OnTrigger;

    public GameObject ShootPoint;

    [SerializeField] public NetworkVariable<bool> Enabled  = new NetworkVariable<bool>(true);
    // public bool Enabled = false;
    public int ID = 0;

    // // Shooter Type: 0 - 17mm 1 - 42mm
    public NetworkVariable<int> Type      = new NetworkVariable<int>(0);


    // Shooter 17mm Mode: 0 - None 1 - Boost 2 - CD 3 - Speed
    // Shooter 42mm Mode: 0 - None 1 - Boost 2 - Speed
    public NetworkVariable<int> Mode      = new NetworkVariable<int>(0);

    public RobotPerformanceSO GimbalPerformance;
    public NetworkVariable<int> Level             = new NetworkVariable<int>(0);

    public NetworkVariable<float> HeatLimit        = new NetworkVariable<float>(0f);
    public NetworkVariable<float> Heat             = new NetworkVariable<float>(0f);
    public NetworkVariable<int> CD               = new NetworkVariable<int>(0);
    public NetworkVariable<int> SpeedLimit       = new NetworkVariable<int>(0);

    private Material Lightbar;
    [SerializeField] private GameObject LightbarObject;

    [SerializeField] private GameObject UI;
    [SerializeField] private TextMeshProUGUI Speed;
    [SerializeField] private TextMeshProUGUI Ammo;
    [SerializeField] private BarController HeatBar;

    [SerializeField] private GameObject BlurBackground;
    [SerializeField] private GameObject OverHeat;

    void Awake()
    {
        if (PacificSelectionState.MujocoMode && UI != null) UI.SetActive(false);
        // Legacy roster: hide the card until LegacyRoster selects this car.
        if (PacificSelectionState.LegacyRoster) ApplyLegacyCard(false);
    }

    void Start()
    {
        // UI.SetActive(IsOwner);
        Lightbar = LightbarObject.GetComponent<Renderer>().material;
        Lightbar.SetFloat("_FillRate", 0f);
        Lightbar.SetFloat("_Boarder", LightbarObject.GetComponent<MeshFilter>().mesh.bounds.size.z / 2f);
    }

    void Update()
    {
        // Update light effects on shooter components according to head and heatlimit

        LightbarObject.GetComponent<Renderer>().material.SetFloat("_FillRate", Heat.Value / HeatLimit.Value);

        HeatBar.SetValue(Heat.Value);
        HeatBar.SetMaxValue(HeatLimit.Value);
        
        if (Heat.Value < HeatLimit.Value / 2)
        {
            HeatBar.SetColor(Color.white);
        } else if (Heat.Value >= HeatLimit.Value / 2 && Heat.Value < HeatLimit.Value * 3/4) {
            HeatBar.SetColor(Color.yellow);
        } else {
            HeatBar.SetColor(Color.red);
        }

        if (PacificSelectionState.MujocoMode)
        {
            if (UI != null && UI.activeSelf) UI.SetActive(false);
            if (BlurBackground != null) BlurBackground.SetActive(false);
            if (OverHeat != null) OverHeat.SetActive(false);
        }
        else
        {
            BlurBackground.SetActive(Heat.Value > HeatLimit.Value);
            OverHeat.SetActive(Heat.Value > HeatLimit.Value);
        }
    }

    public void PullTrigger()
    {
        if (!Enabled.Value) return;

        // Debug.Log("[ShooterController] PullTrigger");
        OnTrigger(ID, ShootPoint.transform.position, ShootPoint.transform.right * SpeedLimit.Value * 10);
    }

    public void SetAmmo(int ammo, int ammoLimit)
    {
        Ammo.text = ammo.ToString() + "/" + ammoLimit.ToString();
    }

    public void SetFaction(Faction fac)
    {
        if (fac == Faction.Red)
        {
            Lightbar.SetColor("_FillColor", Color.red);
        } else if (fac == Faction.Blue) {
            Lightbar.SetColor("_FillColor", Color.blue);
        } else {
            Lightbar.SetColor("_FillColor", Color.white);
        }
    }

    public void ToggleUI()
    {
        if (PacificSelectionState.MujocoMode)
        {
            if (UI != null) UI.SetActive(false);
            return;
        }
        if (PacificSelectionState.LegacyRoster)
        {
            // Every roster vehicle initialises its shooter, but only the car
            // the operator drives may show a card. Training has a single car;
            // RMUC has fourteen, and fourteen screen-space cards stack into
            // one garbled "Shooter1" panel over the crosshair.
            RefereeController referee = GetComponentInParent<RefereeController>();
            ApplyLegacyCard(referee != null && referee.RobotID.Value == LegacyRoster.SelectedId);
            return;
        }
        UI.SetActive(!UI.activeSelf);
    }

    /// <summary>
    /// Shows the ammo/speed card for the vehicle the legacy roster selected.
    /// The original prefab also carries a decorative arc (ShooterInfoLine)
    /// inside the same canvas; the official HUD already draws its own
    /// crosshair, so keep only the card.
    /// </summary>
    public void SetLegacyCardVisible(bool visible)
    {
        ApplyLegacyCard(visible);
    }

    private void ApplyLegacyCard(bool visible)
    {
        if (UI == null) return;
        Transform info = null;
        foreach (Transform child in UI.GetComponentsInChildren<Transform>(true))
        {
            if (child.name == "Info") { info = child; break; }
        }
        if (info != null)
        {
            Transform line = info.Find("Line");
            if (line != null) line.gameObject.SetActive(false);
        }
        UI.SetActive(visible);
    }

    public void Reset()
    {
        Level.Value = 0;

        Heat.Value = 0;
        // Reset runs for every registered robot when the match starts. A
        // shooter whose performance profile was never applied has no arrays
        // here; dereferencing it would abort StartGame for the whole match.
        if (GimbalPerformance == null || GimbalPerformance.maxHeat == null ||
            GimbalPerformance.coolDown == null || GimbalPerformance.shootSpeed == null ||
            GimbalPerformance.maxHeat.Length == 0 || GimbalPerformance.coolDown.Length == 0 ||
            GimbalPerformance.shootSpeed.Length == 0)
        {
            Debug.LogWarning($"[ShooterController] Shooter {ID} has no performance profile; keeping current limits.");
            return;
        }
        HeatLimit.Value = GimbalPerformance.maxHeat[Level.Value];
        CD.Value = GimbalPerformance.coolDown[Level.Value];
        SpeedLimit.Value = GimbalPerformance.shootSpeed[Level.Value];
    }
}
