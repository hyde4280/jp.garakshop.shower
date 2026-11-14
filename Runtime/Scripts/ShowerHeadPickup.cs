using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.Components;

// Hand-held shower head (made for the 801 bath of this world, Issue #39 follow-up, added 2026-10-20).
//
// Lives on the pickup root (Rigidbody kinematic + trigger BoxCollider + VRCPickup + VRCObjectSync).
// The water (ParticleSystem, world space) and the water-stream Contact Senders are children of the
// head, so they follow it and spray where the head points. ON/OFF stays in ShowerController (one
// synced bool, one writer); the pickup's use button only asks it to toggle, like the wall switch.
//
// ---- RELEASE = BACK INTO THE HOLDER ----
// Dropping the head (or being dropped by ShowerHose when the hose is pulled past its length; the
// bathroom walls are NOT a limit, the head may be held outside the box) returns it to the holder
// with VRCObjectSync.Respawn(): the object sync spawn pose IS the holder pose, Respawn() is synced
// and teleports remote copies (no interpolated flight). There is no "lie where it fell" state, so
// nobody can leave the head floating outside the bath. A slow poll (_Poll) is the backstop for every other way the head can end up away from
// the holder without an OnDrop on this client (the holder leaves the instance, the owner changes):
// the owner returns it after two polls in a row away from the holder while nobody holds it.
//
// ---- OWNERSHIP ----
// VRChat gives the object to whoever grabs it; this script never calls Networking.SetOwner (doing it
// while held drops the pickup). Respawn() is only issued by the owner.
//
// ---- WAKING THE HOSE ----
// ShowerHose redraws only while the head moves and turns itself off when it is still. Locally the
// pickup events wake it at once; on the other clients the head is moved by VRCObjectSync without
// any event, so _Poll (4 Hz) wakes the hose when the head is no longer where the hose was drawn.
//
// VRC_Pickup events only reach UdonBehaviours on the pickup's own GameObject, and VRCObjectSync on
// that GameObject forces Continuous sync, hence NoVariableSync and no synced fields here.
// AutoHold must be Yes, otherwise OnPickupUseDown never fires on desktop.
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class ShowerHeadPickup : UdonSharpBehaviour
{
    [Tooltip("ON/OFF of the shower (the wall switch). The use button calls its _RequestToggle().")]
    public ShowerController controller;

    [Tooltip("Hose drawer / limit checker (separate object so its per-frame work can be switched off).")]
    public ShowerHose hose;

    [Tooltip("VRCPickup on this object.")]
    public VRCPickup pickup;

    [Tooltip("VRCObjectSync on this object. Its spawn pose is the holder pose.")]
    public VRCObjectSync objectSync;

    [Tooltip("Seconds between the background checks (hose wake-up on remote clients, return-to-holder backstop).")]
    public float pollInterval = 0.25f;

    [Tooltip("Distance (m) from the holder pose that counts as 'away'.")]
    public float holderTolerance = 0.01f;

    [Tooltip("Enables [SHOWERHEAD] console logging. Turn OFF before uploading.")]
    public bool debugLog = false;

    private Vector3 holderPosition;
    private Quaternion holderRotation;
    private bool localHeld;
    private int awayPolls;

    private void Start()
    {
        // The scene places the head in the holder; VRCObjectSync records the same pose as its spawn.
        holderPosition = transform.position;
        holderRotation = transform.rotation;
        SendCustomEventDelayedSeconds(nameof(_Poll), pollInterval);
    }

    public override void OnPickup()
    {
        localHeld = true;
        awayPolls = 0;
        if (hose != null) hose._Wake();
        if (debugLog) Debug.Log("[SHOWERHEAD] picked up frame=" + Time.frameCount);
    }

    public override void OnDrop()
    {
        localHeld = false;
        if (hose != null) hose._Wake();
        // One frame later, so the pickup system has finished releasing the object.
        SendCustomEventDelayedFrames(nameof(_ReturnToHolder), 1);
        if (debugLog) Debug.Log("[SHOWERHEAD] dropped -> return to holder frame=" + Time.frameCount);
    }

    public override void OnPickupUseDown()
    {
        if (controller != null) controller._RequestToggle();
    }

    // True only on the client whose hand holds the head (pickup events are local).
    public bool _IsLocallyHeld()
    {
        return localHeld;
    }

    public void _ReturnToHolder()
    {
        if (localHeld) return;
        if (!Networking.IsOwner(gameObject)) return;
        if (objectSync != null) objectSync.Respawn();
        else transform.SetPositionAndRotation(holderPosition, holderRotation);
        if (hose != null) hose._Wake();
    }

    public void _Poll()
    {
        SendCustomEventDelayedSeconds(nameof(_Poll), pollInterval);

        if (hose != null && !hose.enabled && hose._HeadMovedSinceDraw()) hose._Wake();

        if (localHeld || !Networking.IsOwner(gameObject) || (pickup != null && pickup.IsHeld))
        {
            awayPolls = 0;
            return;
        }
        bool away = (transform.position - holderPosition).sqrMagnitude > holderTolerance * holderTolerance
                    || Quaternion.Angle(transform.rotation, holderRotation) > 2f;
        if (!away)
        {
            awayPolls = 0;
            return;
        }
        awayPolls++;
        if (awayPolls >= 2)
        {
            awayPolls = 0;
            if (debugLog) Debug.Log("[SHOWERHEAD] owner found the head away from the holder, nobody holds it -> return");
            _ReturnToHolder();
        }
    }
}
