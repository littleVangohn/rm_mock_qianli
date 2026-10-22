using System.Collections;
using System.Collections.Generic;
using UnityEngine; 
using Unity.Netcode;

public class BulletManager : NetworkBehaviour
{
    [SerializeField] private GameObject Bullet_17mm;
    [SerializeField] private GameObject Bullet_42mm;

    void Start()
    {

    }

    void OnEnable()
    {
        RefereeController.OnShoot += Shoot;
    }

    void OnDisable()
    {
        RefereeController.OnShoot -= Shoot;
    }

    void Shoot(int shooterID, int shooterType, int robotID, Vector3 userPosition, Vector3 shootVelocity)
    {
        Quaternion userDirection = Quaternion.Euler(shootVelocity.x, shootVelocity.y, shootVelocity.z);

        GameObject _bullet;

        switch (shooterType)
        {
            case 0:
                _bullet = Instantiate(Bullet_17mm, userPosition, userDirection);
                break;
            case 1:
                _bullet = Instantiate(Bullet_42mm, userPosition, userDirection);
                break;
            default:
                Debug.LogError($"[BulletManager] Unknown shooterType {shooterType}!");
                return;
        }

        _bullet.GetComponent<Rigidbody>().isKinematic = false;
        _bullet.GetComponent<Rigidbody>().linearVelocity = shootVelocity;
        _bullet.SetActive(true);
        _bullet.GetComponent<NetworkObject>().Spawn();
        _bullet.GetComponent<BulletController>().attackerID.Value = robotID;
        AddVisibleTracer(_bullet);
    }

    // Source simulator: 10 units per metre (17 mm prefab scale 0.17,
    // 42 mm prefab scale 0.42). BuildFriendlyProjectile authors its geometry
    // in metres, so the visual needs this factor to reach real scene size.
    private const float UnitsPerMetre = 10f;
    private const float SourceRoundDiameterMetres = 0.0425f;

    private static GameObject tracerTemplate;

    /// <summary>
    /// The OSS round is 17 mm and invisible from the operator camera. Attach
    /// the same emissive projectile the reference build draws: a lit core, a
    /// 5%-larger transparent halo and a short trail, built by
    /// NativeSceneBuilder.BuildFriendlyProjectile, scaled from metres to the
    /// source simulator's scene units.
    /// </summary>
    public static void AddVisibleTracer(GameObject bullet)
    {
        if (bullet == null || bullet.transform.Find("Visible Tracer") != null) return;

        if (tracerTemplate == null)
            tracerTemplate = NativeSceneBuilder.BuildFriendlyProjectile(null);

        GameObject visual = Object.Instantiate(tracerTemplate, bullet.transform, false);
        visual.name = "Visible Tracer";
        visual.transform.localPosition = Vector3.zero;
        visual.transform.localRotation = Quaternion.identity;

        // Same conversion as the reference bridge: the authored 42.5 mm sphere
        // becomes radius-metres / 21.25 mm * 10 units/m in world space. Since
        // this visual is parented to a bullet scaled 0.17/0.42, divide by that
        // scale to keep the authored geometry at true size.
        float bulletScale = Mathf.Max(0.05f, bullet.transform.lossyScale.x);
        float bulletDiameterMetres = bulletScale / UnitsPerMetre;
        float roundScale = bulletDiameterMetres / SourceRoundDiameterMetres;
        float worldScale = UnitsPerMetre * roundScale;
        visual.transform.localScale = Vector3.one * (worldScale / bulletScale);

        // CreatePrimitive leaves colliders behind for one frame. As children
        // of the bullet's Rigidbody they become part of its compound shape
        // and can stop the round inside the barrel; kill them now.
        foreach (Collider collider in visual.GetComponentsInChildren<Collider>(true))
        {
            collider.enabled = false;
            Object.Destroy(collider);
        }

        // A 42.5 mm core is still only a few pixels at speed, so give the
        // tracer a little extra presence and keep the streak alive long
        // enough for the eye to catch it.
        visual.transform.localScale *= 1.5f;
        TrailRenderer trail = visual.GetComponent<TrailRenderer>();
        if (trail != null)
        {
            trail.Clear();
            trail.time = 0.25f;
            trail.emitting = true;
        }
        visual.SetActive(true);
    }
}