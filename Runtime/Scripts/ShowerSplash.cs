using UdonSharp;
using UnityEngine;
using VRC.SDKBase;

// Splashes where the shower stream meets an avatar (and optionally a water surface). Added 2026-10-22
// for the 801 shower of this world.
//
// Lives on the Splash object of the shower (a ParticleSystem with no emission of its own; this
// script calls Emit()); in this world Residence_8F/801_Shower/Splash. It is on the shower side, not
// on the bath water, because the tub may be empty (the ripple receiver is inactive then) and the
// shower still splashes on bodies.
//
// Coordinates (streamEndY, roomMin/Max, waterY, tubCenter) are WORLD coordinates. The C# defaults
// (2026-11-14, Issue #77) are neutral values around the world origin; a real shower sets them in the
// scene / Prefab (the 801 values are stored in the scene).
//
// WHY NOT PARTICLE COLLISION: players are not ordinary colliders, so the water particles cannot
// collide with avatars (they still pass through them, which is fine). Instead every client
// computes, locally and for every player near the shower (nothing is synced):
//   1. the stream's centre line: the ballistic path of a drop leaving the nozzle (Water transform)
//      along +forward at streamSpeed under gravity, until it reaches streamEndY (the stop plane of
//      the water particles) or leaves the bathroom box (the wall / ceiling planes), sampled as a
//      polyline. The stream radius grows with distance (cone of the water particles);
//   2. each player's body as capsules between humanoid bones (hips-chest, chest-neck, neck-head
//      top, thighs, shins, upper arms, forearms extended to the finger tips; one capsule from the
//      feet to the head for avatars without humanoid bones), radii scaled by avatar eye height;
//   3. walking the stream from the nozzle, the first sample that is within (capsule radius +
//      stream radius) of a capsule is where the water hits that player: the splash point is on
//      the capsule surface facing the stream. One point per player, at most MAX_PLAYERS players.
//   Where the stream reaches the bath water surface before hitting anybody, it splashes there too
//   and (tub filled) asks the ripple receiver (ShowerRippleReceiver; in this world BathWaterRipples)
//   for a ring source at that point.
// The geometry is re-evaluated every checkInterval seconds (the head is moved by hand); between
// checks the last points keep emitting. Particles are emitted every frame at splashRate per point
// by moving the Splash transform to each point (world-space simulation) and calling Emit().
//
// Runs only while the water is actually coming out: the water particle system is emitting AND its
// emission module is enabled (ShowerController Play/Stop = ON/OFF, ShowerHose switches emission
// off while the head is outside the bathroom box), and only while the local player is within
// activeRadius of the nozzle.
//
// Nothing depends on build-time processing; no external pack type is referenced.
namespace GarakShop.Shower
{
    [UdonBehaviourSyncMode(BehaviourSyncMode.None)]
    public class ShowerSplash : UdonSharpBehaviour
    {
        [Header("Stream (the shower water)")]
        [Tooltip("Water stream ParticleSystem. Splashes only while it is emitting with emission enabled.")]
        public ParticleSystem waterParticles;
        [Tooltip("Nozzle: the stream leaves its position along +forward. Usually the Water transform.")]
        public Transform nozzle;
        [Tooltip("Initial speed of the stream (m/s, mean start speed of the water particles).")]
        public float streamSpeed = 1.8f;
        [Tooltip("Gravity acting on the stream (m/s^2, gravity modifier 1 of the water particles).")]
        public float gravity = 9.81f;
        [Tooltip("The stream ends at this world height (stop plane of the water particles, tub bottom / floor).")]
        public float streamEndY = 0f;
        [Tooltip("Longest flight time of the stream (s, start lifetime of the water particles).")]
        public float maxFlightTime = 0.9f;
        [Tooltip("Distance between samples along the stream (m).")]
        public float sampleSpacing = 0.06f;
        [Tooltip("Stream radius at the nozzle (m).")]
        public float streamRadius = 0.02f;
        [Tooltip("Stream radius growth per metre travelled (tan of the cone half angle, 5 deg = 0.087).")]
        public float streamSpread = 0.087f;
        [Tooltip("The stream also ends where it leaves this world-space box (the wall / ceiling planes). Minimum corner.")]
        public Vector3 roomMin = new Vector3(-1f, 0f, -1f);
        [Tooltip("Maximum corner of the box (world).")]
        public Vector3 roomMax = new Vector3(1f, 2.5f, 1f);

        [Header("Bodies")]
        [Tooltip("The local player must be within this distance of the nozzle (m), else nothing runs.")]
        public float activeRadius = 4.0f;
        [Tooltip("Seconds between geometry checks (players, stream, hit points).")]
        public float checkInterval = 0.1f;
        [Tooltip("Extra distance (m) added to every body capsule radius.")]
        public float hitMargin = 0.0f;
        [Tooltip("Scale the body radii by the avatar's eye height (1.5 m = 1x).")]
        public bool scaleWithAvatar = true;

        [Header("Splash particles")]
        [Tooltip("ParticleSystem emitting the droplets (world space, no emission of its own). Moved to each point.")]
        public ParticleSystem splash;
        [Tooltip("Droplets per second at each body hit point.")]
        public float splashRate = 130f;
        [Tooltip("Droplets per second where the stream hits the bath water.")]
        public float waterSplashRate = 35f;
        [Tooltip("How much the splash leans back against the stream (0 = along the body surface normal).")]
        public float backSpray = 0.5f;

        [Header("Bath water (optional)")]
        [Tooltip("Receiver of the points where the stream meets the water (a ShowerRippleReceiver, e.g. the world's tub ripples). Used only while its GameObject is active (tub filled). Empty = no water.")]
        public ShowerRippleReceiver ripples;
        [Tooltip("Water plane of the tub (world y). Hits up to waterBand above it count as on the water.")]
        public float waterY = 0.5f;
        [Tooltip("Height band (m) above waterY in which a body hit also ripples the water.")]
        public float waterBand = 0.05f;
        [Tooltip("Centre of the inner tub (world x, z) where the stream lands in the water.")]
        public Vector2 tubCenter = new Vector2(0f, 0f);
        [Tooltip("Half size (x, z) of the inner tub at the water plane (m).")]
        public Vector2 tubHalfSize = new Vector2(0.200f, 0.419f);

        [Tooltip("Enables [SHOWERSPLASH] console logging. Turn OFF before uploading.")]
        public bool debugLog = false;

        private const int MAX_PLAYERS = 4;
        private const int SEGMENTS = 11;
        private const int MAX_SAMPLES = 32;
        private const int MAX_POINTS = MAX_PLAYERS + 1;

        private VRCPlayerApi[] allPlayers;
        private VRCPlayerApi[] near;
        private int nearCount;

        // Body capsules of one player (rebuilt per player).
        private Vector3[] segA;
        private Vector3[] segB;
        private float[] segR;
        private float[] baseR;

        // Stream samples.
        private Vector3[] sp;
        private float[] sr;
        private int sampleCount;
        private Vector3 sMin, sMax;

        // Emission points.
        private Vector3[] ptPos;
        private Vector3[] ptDir;
        private float[] ptRate;
        private float[] ptAcc;
        private int ptCount;

        private float nextCheck;
        private bool running;
        private bool ready;

        void Start()
        {
            Init();
        }

        private void Init()
        {
            if (ready) return;
            allPlayers = new VRCPlayerApi[16];
            near = new VRCPlayerApi[MAX_PLAYERS];
            segA = new Vector3[SEGMENTS];
            segB = new Vector3[SEGMENTS];
            segR = new float[SEGMENTS];
            baseR = new float[SEGMENTS];
            // hips-chest, chest-neck, neck-head top, thighs, shins, upper arms, forearms(+hand)
            baseR[0] = 0.11f; baseR[1] = 0.10f; baseR[2] = 0.09f;
            baseR[3] = 0.07f; baseR[4] = 0.07f; baseR[5] = 0.05f; baseR[6] = 0.05f;
            baseR[7] = 0.045f; baseR[8] = 0.045f; baseR[9] = 0.04f; baseR[10] = 0.04f;
            sp = new Vector3[MAX_SAMPLES];
            sr = new float[MAX_SAMPLES];
            ptPos = new Vector3[MAX_POINTS];
            ptDir = new Vector3[MAX_POINTS];
            ptRate = new float[MAX_POINTS];
            ptAcc = new float[MAX_POINTS];
            useBuf = new bool[SEGMENTS];
            ready = true;
        }

        void OnDisable()
        {
            if (!ready) return;
            StopAll();
        }

        void Update()
        {
            if (!ready) Init();
            float now = Time.time;
            if (now >= nextCheck)
            {
                nextCheck = now + checkInterval;
                Check();
            }
            if (ptCount == 0 || splash == null) return;

            float dt = Time.deltaTime;
            Transform st = splash.transform;
            for (int i = 0; i < ptCount; i++)
            {
                ptAcc[i] += ptRate[i] * dt;
                int n = (int)ptAcc[i];
                if (n <= 0) continue;
                ptAcc[i] -= n;
                st.SetPositionAndRotation(ptPos[i], Quaternion.LookRotation(ptDir[i]));
                splash.Emit(n);
            }
        }

        private void StopAll()
        {
            if (ptCount > 0 && debugLog) Debug.Log("[SHOWERSPLASH] stop frame=" + Time.frameCount);
            ptCount = 0;
            if (running && ripples != null) ripples._SetShowerCount(0);
            running = false;
        }

        private bool WaterRunning()
        {
            if (waterParticles == null || nozzle == null) return false;
            if (!waterParticles.isEmitting) return false;
            ParticleSystem.EmissionModule em = waterParticles.emission;
            return em.enabled;
        }

        private void Check()
        {
            if (!WaterRunning()) { StopAll(); return; }
            VRCPlayerApi local = Networking.LocalPlayer;
            if (!Utilities.IsValid(local)) { StopAll(); return; }
            Vector3 n0 = nozzle.position;
            if ((local.GetPosition() - n0).sqrMagnitude > activeRadius * activeRadius) { StopAll(); return; }

            BuildStream();
            if (sampleCount < 2) { StopAll(); return; }
            GatherPlayers();

            // With the tub filled, the stream ends where it enters the water (nothing under the
            // surface is hit; a shin under water stays dry).
            bool tubFilled = ripples != null && ripples.gameObject.activeInHierarchy;
            int waterSample = tubFilled ? WaterCrossing() : -1;
            int limit = waterSample >= 0 ? waterSample : sampleCount;

            int pc = 0;
            int firstHit = sampleCount; // earliest sample hit by anybody
            for (int k = 0; k < nearCount; k++)
            {
                VRCPlayerApi p = near[k];
                if (!Utilities.IsValid(p)) continue;
                if (!BuildBody(p)) continue;
                int hitSample = HitPlayer(pc, limit);
                if (hitSample >= 0)
                {
                    if (hitSample < firstHit) firstHit = hitSample;
                    pc++;
                }
            }
            int bodyCount = pc;

            // Where the stream reaches the bath water before hitting anybody.
            int rc = 0;
            if (waterSample >= 0 && firstHit >= waterSample)
            {
                Vector3 w = WaterPoint(waterSample);
                if (pc < MAX_POINTS)
                {
                    ptPos[pc] = w;
                    ptDir[pc] = Vector3.up;
                    ptRate[pc] = waterSplashRate;
                    pc++;
                }
                ripples._SetShowerPoint(rc, w.x, w.z, 1f);
                rc++;
            }
            if (tubFilled)
            {
                // Body hits just above the water surface also ripple it.
                for (int i = 0; i < bodyCount && rc < 4; i++)
                {
                    Vector3 q = ptPos[i];
                    if (q.y < waterY + waterBand && InsideTub(q))
                    {
                        ripples._SetShowerPoint(rc, q.x, q.z, 0.6f);
                        rc++;
                    }
                }
                ripples._SetShowerCount(rc);
            }

            if (debugLog && pc != ptCount) Debug.Log("[SHOWERSPLASH] points " + ptCount + " -> " + pc + " samples=" + sampleCount + " players=" + nearCount + " frame=" + Time.frameCount);
            for (int i = ptCount; i < pc; i++) ptAcc[i] = 0f;
            ptCount = pc;
            running = true;
        }

        // Ballistic centre line of the stream, clipped at streamEndY and at the bathroom box.
        private void BuildStream()
        {
            Vector3 p0 = nozzle.position;
            Vector3 v = nozzle.forward * streamSpeed;
            float t = 0f;
            Vector3 p = p0;
            sampleCount = 0;
            sMin = p0; sMax = p0;
            float travelled = 0f;
            for (int i = 0; i < MAX_SAMPLES; i++)
            {
                sp[i] = p;
                sr[i] = streamRadius + streamSpread * travelled;
                sampleCount = i + 1;
                sMin = Vector3.Min(sMin, p);
                sMax = Vector3.Max(sMax, p);
                // Advance by about sampleSpacing along the path.
                Vector3 vel = v + Vector3.down * (gravity * t);
                float speed = vel.magnitude;
                float dt = speed > 0.01f ? sampleSpacing / speed : 0.02f;
                float t2 = t + dt;
                if (t2 > maxFlightTime) break;
                Vector3 q = p0 + v * t2 + Vector3.down * (0.5f * gravity * t2 * t2);
                if (q.y < streamEndY || !InsideRoom(q))
                {
                    // Last sample on the boundary (approximately).
                    if (i + 1 < MAX_SAMPLES)
                    {
                        Vector3 e = q;
                        if (q.y < streamEndY && p.y > q.y)
                        {
                            float f = (p.y - streamEndY) / (p.y - q.y);
                            e = p + (q - p) * f;
                        }
                        sp[i + 1] = e;
                        sr[i + 1] = sr[i];
                        sampleCount = i + 2;
                        sMin = Vector3.Min(sMin, e);
                        sMax = Vector3.Max(sMax, e);
                    }
                    break;
                }
                travelled += (q - p).magnitude;
                p = q;
                t = t2;
            }
        }

        private bool InsideRoom(Vector3 p)
        {
            return p.x >= roomMin.x && p.x <= roomMax.x
                && p.y >= roomMin.y && p.y <= roomMax.y
                && p.z >= roomMin.z && p.z <= roomMax.z;
        }

        private bool InsideTub(Vector3 p)
        {
            return Mathf.Abs(p.x - tubCenter.x) <= tubHalfSize.x && Mathf.Abs(p.z - tubCenter.y) <= tubHalfSize.y;
        }

        // Players whose position is near the stream's bounding box (at most MAX_PLAYERS).
        private void GatherPlayers()
        {
            int count = VRCPlayerApi.GetPlayerCount();
            if (allPlayers.Length < count) allPlayers = new VRCPlayerApi[count + 8];
            VRCPlayerApi.GetPlayers(allPlayers);
            nearCount = 0;
            float m = 1.0f;
            for (int i = 0; i < count && i < allPlayers.Length; i++)
            {
                VRCPlayerApi p = allPlayers[i];
                if (!Utilities.IsValid(p)) continue;
                Vector3 pos = p.GetPosition();
                if (pos.x < sMin.x - m || pos.x > sMax.x + m || pos.z < sMin.z - m || pos.z > sMax.z + m) continue;
                if (pos.y > sMax.y + 0.5f || pos.y < sMin.y - 2.5f) continue;
                near[nearCount] = p;
                nearCount++;
                if (nearCount >= MAX_PLAYERS) break;
            }
        }

        // Body capsules of a player. False when nothing usable.
        private bool BuildBody(VRCPlayerApi p)
        {
            float s = 1f;
            if (scaleWithAvatar)
            {
                float eye = p.GetAvatarEyeHeightAsMeters();
                if (eye > 0.05f) s = Mathf.Clamp(eye / 1.5f, 0.4f, 2.5f);
            }
            for (int i = 0; i < SEGMENTS; i++) { segA[i] = Vector3.zero; segB[i] = Vector3.zero; segR[i] = baseR[i] * s + hitMargin; }

            Vector3 hips = p.GetBonePosition(HumanBodyBones.Hips);
            if (hips == Vector3.zero)
            {
                // Generic / no avatar: one capsule from the feet to the head.
                segA[0] = p.GetPosition();
                segB[0] = p.GetTrackingData(VRCPlayerApi.TrackingDataType.Head).position;
                segR[0] = 0.15f * s + hitMargin;
                return segB[0] != Vector3.zero;
            }
            Vector3 chest = p.GetBonePosition(HumanBodyBones.Chest);
            if (chest == Vector3.zero) chest = p.GetBonePosition(HumanBodyBones.Spine);
            Vector3 neck = p.GetBonePosition(HumanBodyBones.Neck);
            Vector3 head = p.GetBonePosition(HumanBodyBones.Head);
            if (neck == Vector3.zero) neck = head;
            segA[0] = hips; segB[0] = chest;
            segA[1] = chest; segB[1] = neck;
            if (head != Vector3.zero && neck != Vector3.zero)
            {
                Vector3 up = head - neck;
                up = up.sqrMagnitude > 1e-6f ? up.normalized : Vector3.up;
                segA[2] = neck; segB[2] = head + up * (0.16f * s);
            }
            segA[3] = p.GetBonePosition(HumanBodyBones.LeftUpperLeg);
            segB[3] = p.GetBonePosition(HumanBodyBones.LeftLowerLeg);
            segA[4] = p.GetBonePosition(HumanBodyBones.RightUpperLeg);
            segB[4] = p.GetBonePosition(HumanBodyBones.RightLowerLeg);
            segA[5] = segB[3];
            segB[5] = p.GetBonePosition(HumanBodyBones.LeftFoot);
            segA[6] = segB[4];
            segB[6] = p.GetBonePosition(HumanBodyBones.RightFoot);
            segA[7] = p.GetBonePosition(HumanBodyBones.LeftUpperArm);
            segB[7] = p.GetBonePosition(HumanBodyBones.LeftLowerArm);
            segA[8] = p.GetBonePosition(HumanBodyBones.RightUpperArm);
            segB[8] = p.GetBonePosition(HumanBodyBones.RightLowerArm);
            Vector3 lh = p.GetBonePosition(HumanBodyBones.LeftHand);
            Vector3 rh = p.GetBonePosition(HumanBodyBones.RightHand);
            if (segB[7] != Vector3.zero && lh != Vector3.zero) lh = lh + (lh - segB[7]).normalized * (0.09f * s);
            if (segB[8] != Vector3.zero && rh != Vector3.zero) rh = rh + (rh - segB[8]).normalized * (0.09f * s);
            segA[9] = segB[7]; segB[9] = lh;
            segA[10] = segB[8]; segB[10] = rh;
            return true;
        }

        // Walks the stream from the nozzle; at the first sample touching a capsule, writes the splash
        // point into slot `slot` and returns the sample index (-1 = no hit).
        private int HitPlayer(int slot, int limit)
        {
            if (slot >= MAX_POINTS) return -1;
            // Capsules that can reach the stream's bounding box at all.
            bool any = false;
            bool[] use = useBuf;
            float pad = streamRadius + streamSpread * 2f;
            for (int j = 0; j < SEGMENTS; j++)
            {
                Vector3 a = segA[j];
                Vector3 b = segB[j];
                use[j] = false;
                if (a == Vector3.zero || b == Vector3.zero) continue;
                float r = segR[j] + pad;
                Vector3 lo = Vector3.Min(a, b);
                Vector3 hi = Vector3.Max(a, b);
                if (hi.x < sMin.x - r || lo.x > sMax.x + r) continue;
                if (hi.y < sMin.y - r || lo.y > sMax.y + r) continue;
                if (hi.z < sMin.z - r || lo.z > sMax.z + r) continue;
                use[j] = true;
                any = true;
            }
            if (!any) return -1;

            for (int i = 0; i < limit; i++)
            {
                Vector3 q = sp[i];
                float best = 1f;
                int bj = -1;
                Vector3 bc = Vector3.zero;
                for (int j = 0; j < SEGMENTS; j++)
                {
                    if (!use[j]) continue;
                    Vector3 a = segA[j];
                    Vector3 ab = segB[j] - a;
                    float l2 = Vector3.Dot(ab, ab);
                    float t = l2 > 1e-8f ? Mathf.Clamp01(Vector3.Dot(q - a, ab) / l2) : 0f;
                    Vector3 c = a + ab * t;
                    float lim = segR[j] + sr[i];
                    float d2 = (q - c).sqrMagnitude;
                    if (d2 < lim * lim)
                    {
                        float rel = Mathf.Sqrt(d2) / lim;
                        if (rel < best) { best = rel; bj = j; bc = c; }
                    }
                }
                if (bj >= 0)
                {
                    Vector3 vin = StreamDir(i);
                    Vector3 nrm = q - bc;
                    nrm = nrm.sqrMagnitude > 1e-6f ? nrm.normalized : -vin;
                    // Water lands on the capsule surface facing the stream.
                    if (Vector3.Dot(nrm, vin) > 0f) nrm = -nrm;
                    Vector3 hit = bc + nrm * segR[bj];
                    Vector3 dir = nrm - vin * backSpray;
                    if (dir.sqrMagnitude < 1e-6f) dir = Vector3.up;
                    ptPos[slot] = hit;
                    ptDir[slot] = dir.normalized;
                    ptRate[slot] = splashRate;
                    return i;
                }
            }
            return -1;
        }

        private bool[] useBuf;

        private Vector3 StreamDir(int i)
        {
            Vector3 d = (i + 1 < sampleCount) ? sp[i + 1] - sp[i] : sp[i] - sp[i - 1];
            return d.sqrMagnitude > 1e-8f ? d.normalized : Vector3.down;
        }

        // First sample index after which the stream is below the water plane, inside the tub (-1 = none).
        private int WaterCrossing()
        {
            for (int i = 1; i < sampleCount; i++)
            {
                if (sp[i - 1].y >= waterY && sp[i].y < waterY)
                {
                    Vector3 w = WaterPoint(i);
                    return InsideTub(w) ? i : -1;
                }
            }
            return -1;
        }

        private Vector3 WaterPoint(int i)
        {
            Vector3 a = sp[i - 1];
            Vector3 b = sp[i];
            float f = (a.y - b.y) > 1e-6f ? (a.y - waterY) / (a.y - b.y) : 0f;
            Vector3 w = a + (b - a) * f;
            w.y = waterY;
            return w;
        }
    }
}
