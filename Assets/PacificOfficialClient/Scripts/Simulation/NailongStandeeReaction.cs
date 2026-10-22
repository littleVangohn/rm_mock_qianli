using System.Collections;
using UnityEngine;

/// <summary>
/// The 奶龙 standee is decoration, so its hit box is a trigger: rounds pass
/// straight through the board. When one does, the front print swaps to the
/// "hit" artwork for one second and then goes back.
/// </summary>
public sealed class NailongStandeeReaction : MonoBehaviour
{
    [SerializeField] private Renderer normalBoard;
    [SerializeField] private Renderer hitBoard;
    [SerializeField] private float hitSeconds = 1f;

    private Coroutine flash;

    public void Configure(Renderer normal, Renderer hit, float seconds)
    {
        normalBoard = normal;
        hitBoard = hit;
        hitSeconds = seconds;
    }

    private void Awake()
    {
        if (hitBoard != null) hitBoard.enabled = false;
    }

    private void OnTriggerEnter(Collider other)
    {
        if (other == null || normalBoard == null || hitBoard == null) return;
        if (other.GetComponentInParent<BulletController>() == null) return;
        if (flash != null) StopCoroutine(flash);
        flash = StartCoroutine(Flash());
    }

    private IEnumerator Flash()
    {
        normalBoard.enabled = false;
        hitBoard.enabled = true;
        yield return new WaitForSeconds(hitSeconds);
        hitBoard.enabled = false;
        normalBoard.enabled = true;
        flash = null;
    }
}
