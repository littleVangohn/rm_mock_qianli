using Unity.Netcode;
using UnityEngine;

/// <summary>
/// The Training Ground scene has no BulletManager, so the OSS referee raises
/// OnShoot but nothing ever spawns a round. This listens to the same event and
/// instantiates the original bullet prefabs locally on the server; the bullets
/// keep their rigidbody, tag and BulletController, so armour damage works
/// exactly like the match scenes (NGO replication is not needed for a host).
/// </summary>
public sealed class LegacyBulletSpawner : MonoBehaviour
{
    private GameObject bullet17;
    private GameObject bullet42;

    private void OnEnable() => RefereeController.OnShoot += OnShoot;
    private void OnDisable() => RefereeController.OnShoot -= OnShoot;

    private void Load()
    {
        if (bullet17 == null) bullet17 = Resources.Load<GameObject>("Bullet-17mm");
        if (bullet42 == null) bullet42 = Resources.Load<GameObject>("Bullet-42mm");
    }

    private void OnShoot(int shooterID, int shooterType, int robotID, Vector3 position, Vector3 velocity)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        Load();
        GameObject prefab = shooterType == 1 ? bullet42 : bullet17;
        if (prefab == null)
        {
            Debug.LogWarning($"[LegacyBullet] Missing prefab for shooter type {shooterType}.");
            return;
        }
        Quaternion rotation = velocity.sqrMagnitude > 0.0001f
            ? Quaternion.LookRotation(velocity) : Quaternion.identity;
        GameObject bullet = Instantiate(prefab, position, rotation);
        Rigidbody body = bullet.GetComponent<Rigidbody>();
        if (body != null)
        {
            body.isKinematic = false;
            body.linearVelocity = velocity;
        }
        BulletController controller = bullet.GetComponent<BulletController>();
        if (controller != null) controller.attackerID.Value = robotID;
        bullet.SetActive(true);
        BulletManager.AddVisibleTracer(bullet);
    }
}
