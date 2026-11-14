using UdonSharp;
using UnityEngine;

// Water sound of the shower (made for the 801 bath of this world, added 2026-10-22).
//
// Lives on the ShowerSound child of the head (in this world
// Residence_8F/801_Shower/ShowerHeadPickup/ShowerHead/ShowerSound) next to a looping AudioSource
// (clip Assets/VRCWorld/Audio/ShowerLoop.wav, a synthesized sound, see
// BlenderSource/scripts/make_shower_loop_sound.py). It is a child of the head, so the sound travels
// with the hand-held head.
//
// ---- WHEN IT PLAYS ----
// Exactly while the water is coming out: the water ParticleSystem is emitting (ShowerController
// Play/Stop = the synced isOn) AND its emission module is enabled (ShowerHose switches it off while
// the head is outside the bathroom box). This is the same condition ShowerSplash uses, so the sound,
// the water and the splashes always agree. Nothing is synced here: every client derives the sound
// from the state it already has, so late joiners hear it as soon as OnDeserialization has applied isOn.
//
// ---- COST ----
// Event driven. ShowerController.ApplyState and ShowerHose.UpdateEmission call _Refresh() when the
// state may have changed. Update runs ONLY while the volume is fading (0.15 s by default) and then
// disables itself, so a steady shower or a silent bath costs nothing per frame.
//
// _Refresh starts with an underscore so it is not on the network surface (a public parameterless
// method without one could be called by any client via SendCustomNetworkEvent).
namespace GarakShop.Shower
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ShowerSound : UdonSharpBehaviour
    {
        [Tooltip("Looping AudioSource (Loop on, Play On Awake off). Volume is driven from here.")]
        public AudioSource source;

        [Tooltip("The shower water particles. The sound plays while they emit with emission enabled.")]
        public ParticleSystem waterParticles;

        [Tooltip("Volume at full level (AudioSource volume while the water runs).")]
        [Range(0f, 1f)]
        public float maxVolume = 0.45f;

        [Tooltip("Seconds for the fade in / out (avoids clicks when starting / stopping).")]
        public float fadeSeconds = 0.15f;

        [Tooltip("Enables [SHOWERSOUND] console logging. Turn OFF before uploading.")]
        public bool debugLog = false;

        // 0..1 fade position, the target it moves to.
        private float level = 0f;
        private float target = 0f;

        // Called by ShowerController (after Play/Stop) and ShowerHose (after the emission switch).
        // Cheap and idempotent.
        public void _Refresh()
        {
            if (source == null) return;
            bool wet = waterParticles != null && waterParticles.isEmitting && waterParticles.emission.enabled;
            float t = wet ? 1f : 0f;
            if (t == target && enabled == false) return;
            target = t;

            if (target > 0.5f && !source.isPlaying)
            {
                source.volume = level * maxVolume;
                source.Play();
            }
            if (debugLog) Debug.Log("[SHOWERSOUND] " + (wet ? "start" : "stop") + " level=" + level + " frame=" + Time.frameCount);
            enabled = true;   // run Update for the fade
        }

        public void Update()
        {
            if (source == null) { enabled = false; return; }

            float step = fadeSeconds > 0.001f ? Time.deltaTime / fadeSeconds : 1f;
            if (level < target) level = Mathf.Min(level + step, target);
            else if (level > target) level = Mathf.Max(level - step, target);
            source.volume = level * maxVolume;

            if (level == target)
            {
                if (target == 0f && source.isPlaying) source.Stop();
                enabled = false;
            }
        }
    }
}
