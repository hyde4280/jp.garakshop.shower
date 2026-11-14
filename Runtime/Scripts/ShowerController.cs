using UdonSharp;
using UnityEngine;
using VRC.SDKBase;
using VRC.SDK3.UdonNetworkCalling;
using VRC.Udon.Common.Interfaces;

// ON/OFF of the shower (made for the 801 bath of this world, Issue #36 item 4 / Issue #39). Added 2026-10-19.
//
// This object carries the interact collider (a small trigger BoxCollider over the mixer tap on
// the tub rim). ON: the water ParticleSystem plays and the water-stream Contact Sender object is
// activated. OFF: emission stops (drops already in the air finish falling) and the sender object
// is deactivated at once.
//
// The sender (VRCContactSender, Collision Tag "KZBubble_Shower") is what lets avatar gimmicks
// that listen for that tag (KINYOUZAKKA "あわあわギミック": Constant receivers that wash the
// bubbles off) react to the world shower. The world only sends; nothing here receives contacts.
//
// ---- NETWORKED, AND WHY IT NEVER TAKES OWNERSHIP ----
// Same model as CurtainToggle / RefrigeratorDoor: ownership stays wherever VRChat put it and the
// owner is the ONLY writer of the synced bool. A non-owner's Interact() asks the owner via
// SendCustomNetworkEvent(Owner, _NetToggle); Networking.SetOwner() is avoided on purpose
// (competing claims can split-brain). A dropped request is one missed click.
// Late joiners: OnDeserialization (and Start) apply the synced state; nothing is replayed.
//
// Entry-point names: Interact() compiles to "_interact", OnDeserialization to
// "_onDeserialization"; the network receiver is the parameterless "_NetToggle" (not mangled).
//
// The hand-held shower head (ShowerHeadPickup, added 2026-10-20) toggles the SAME synced bool:
// its "use" button calls _RequestToggle(), which is exactly what Interact() does. There is one
// state and one writer; the switch and the head are two buttons for it.
[UdonBehaviourSyncMode(BehaviourSyncMode.Manual)]
public class ShowerController : UdonSharpBehaviour
{
    [Tooltip("Water stream particles (Play On Awake OFF).")]
    public ParticleSystem waterParticles;

    [Tooltip("GameObject holding the VRCContactSender(s) of the water stream. Active only while the shower is ON.")]
    public GameObject contactSenderRoot;

    [Tooltip("Water sound (ShowerSound on the head). Told to re-check after every Play/Stop; optional.")]
    public ShowerSound showerSound;

    public string onPrompt = "シャワーを出す";
    public string offPrompt = "シャワーを止める";

    [Tooltip("Enables [SHOWER] console logging. Turn OFF before uploading.")]
    public bool debugLog = false;

    // ---- Synced state. WRITTEN ONLY BY THE OWNER, in ApplyToggle(). ----
    [UdonSynced] private bool isOn;

    private bool hasApplied = false;
    private bool appliedIsOn;

    private const float SEND_DEBOUNCE_SECONDS = 0.25f;
    private float lastSendTime = -999f;

    void Start()
    {
        ApplyState();
    }

    public override void Interact()
    {
        _RequestToggle();
    }

    // Local entry point shared by the switch (Interact) and the shower head's use button
    // (ShowerHeadPickup.OnPickupUseDown). The leading underscore keeps it off the network surface.
    public void _RequestToggle()
    {
        if (Networking.IsOwner(gameObject))
        {
            if (debugLog) Debug.Log("[SHOWER] " + name + " toggle request (owner) -> " + (!isOn ? "ON" : "OFF") + " frame=" + Time.frameCount);
            ApplyToggle();
            return;
        }

        // Non-owner: ASK, never write. The water changes only when the owner's packet arrives.
        if (Time.time - lastSendTime < SEND_DEBOUNCE_SECONDS) return;
        lastSendTime = Time.time;
        if (debugLog) Debug.Log("[SHOWER] " + name + " toggle request (non-owner) -> _NetToggle to " + OwnerName() + " frame=" + Time.frameCount);
        SendCustomNetworkEvent(NetworkEventTarget.Owner, nameof(_NetToggle));
    }

    // Network-event receiver (owner only). Re-validates InNetworkCall, the caller, and
    // ownership at ARRIVAL time; a request that lands on a non-owner is dropped.
    [NetworkCallable(10)]
    public void _NetToggle()
    {
        if (!NetworkCalling.InNetworkCall) return;
        var caller = NetworkCalling.CallingPlayer;
        if (caller == null || !caller.IsValid()) return;
        if (!Networking.IsOwner(gameObject)) return;

        if (debugLog) Debug.Log("[SHOWER] " + name + " [owner] _NetToggle from " + caller.displayName + " -> " + (!isOn ? "ON" : "OFF") + " frame=" + Time.frameCount);
        ApplyToggle();
    }

    // OWNER ONLY - both callers check IsOwner first.
    private void ApplyToggle()
    {
        isOn = !isOn;
        RequestSerialization();
        // OnDeserialization never fires on the sender, so the owner applies its own state here.
        ApplyState();
    }

    public override void OnDeserialization()
    {
        if (debugLog) Debug.Log("[SHOWER] " + name + " OnDeserialization isOn=" + isOn + " frame=" + Time.frameCount + " owner=" + OwnerName());
        ApplyState();
    }

    // Idempotent: re-applying the same state does not restart the particles.
    private void ApplyState()
    {
        if (contactSenderRoot != null) contactSenderRoot.SetActive(isOn);

        if (waterParticles != null && (!hasApplied || appliedIsOn != isOn))
        {
            if (isOn) waterParticles.Play();
            else waterParticles.Stop();   // stop emitting; drops in the air finish falling
        }
        hasApplied = true;
        appliedIsOn = isOn;

        // The water sound follows the particles (emitting + emission enabled); see ShowerSound.
        if (showerSound != null) showerSound._Refresh();

        // InteractionText is runtime-only (not serialized), so it must be assigned from code.
        InteractionText = isOn ? offPrompt : onPrompt;
    }

    private string OwnerName()
    {
        var o = Networking.GetOwner(gameObject);
        return (o == null) ? "?" : o.displayName;
    }
}
