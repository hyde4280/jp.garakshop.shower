using UdonSharp;
using UnityEngine;
using VRC.SDK3.Components;

// Stretchy hose between the mixer tap and the hand-held shower head (added 2026-10-20 for the 801 bath
// of this world).
//
// ---- WHAT IS DRAWN ----
// A LineRenderer (world space, width = hose diameter, lit material) along a curve from hoseStart
// (fixed under the mixer) to hoseEnd (a child of the head at the bottom of its ferrule, the hose
// leaves the head along -up). The mixer mesh must not contain a hose of its own: this is the only
// hose. (In this world the 801 mixer uses a mesh derived from the bath pack without its hose and
// head, made by a world-only editor tool.)
//
// Coordinates (startAimPoint, roomMin/Max, the height map) are WORLD coordinates. The C# defaults
// (2026-11-14, Issue #77) are neutral values around the world origin; a real shower sets them in the
// scene / Prefab (the 801 values are stored in the scene).
//
// The shape is a cheap stand-in for a hanging hose, not a simulation (stateless: every client draws
// the same curve from the synced head pose). Points: N = 2 * samplesPerHalf + 1, evenly spaced.
//   0. Fixed ends (2026-10-25): the first segment is a straight stub leaving the mixer along the exit
//      direction (startAimPoint's horizontal direction turned by startYawDegrees, tilted down by
//      startPitchDegrees: diagonally down towards the tub, clear of the basin), the last one is a
//      straight stub into the head along hoseEnd.up. Both are one segment (hoseLength / (N-1)) long.
//   1. Sag point M: the lowest point of the ellipsoid with foci S1 and E1 (stub ends) whose string
//      length is the rest of the hose. A real hose is stiffer than a string and bows away from the
//      wall it hangs from, so M is pushed along pushDirection in proportion to the slack.
//   2. Two cubic Hermite segments S1->M->E1, re-sampled to even spacing. At M the tangent is
//      horizontal when slack and turns towards the chord as the hose tightens.
//   3. Bend smoothing (2026-10-25): the segment directions are smoothed bendSmoothIterations times
//      (each direction averaged with its neighbours 1:2:1, the stub directions fixed), keeping the
//      chord S1->E1 closed, which spreads tight bends of the Hermite pair (e.g. at M) along the hose.
//   4. While the head is inside the bathroom box, every point is kept inside the box and above a height
//      map of the bath (tub rim, tub bottom, floor, toilet, basin) baked by the setup script, and each
//      segment is lifted (at most segmentLiftMax per segment) so its thirds are above it too, so the
//      hose drapes over the rim instead of cutting through it. Points within clampSkipRadius of the two
//      ends are left alone. Then bendRelaxIterations passes pull every point whose three-point bend
//      radius is below minBendRadius towards its neighbours' midpoint, each followed by the clamp.
//      Finally up to despikePasses passes soften points the clamp left bent sharper than despikeAngle
//      (one point lifted onto the tub rim / basin with its neighbours below) by lifting the neighbours.
//      With the head outside the box nothing is corrected (see below).
// Contact is only the point/segment clamp above: where the hose is pressed onto the tub rim, the
// basin or the floor the bend radius can still be well below minBendRadius (measured 2026-10-25).
//
// ---- LIMITS (only on the client that holds the head) ----
// The pickup follows the hand, so the head is not clamped; it is RELEASED (pickup.Drop(), which makes
// ShowerHeadPickup put it back into the holder) ONLY when the straight distance hoseStart -> hoseEnd
// exceeds hoseLength + lengthTolerance. The bathroom box is NOT a release condition (2026-10-20,
// user request: the walls and the ceiling are invisible colliders in VRChat, so a hand that merely
// pokes through them must not make the head jump back into the holder with no visible cause).
//
// ---- HEAD OUTSIDE THE BATHROOM BOX (headRoot, hoseEnd or nozzle outside roomMin/roomMax) ----
//   - Contact senders: all deactivated (avatars outside the bath are never touched).
//   - Water: particle emission is switched off (emission.enabled). The wall / ceiling collision
//     planes only catch particles that cross them from the inside, so particles born outside would
//     fly on and be seen through the wall. Particles already in the air keep falling and die on
//     the planes as before. Play / Stop stay with ShowerController (it owns the ON/OFF state).
//   - Hose: no correction at all (neither the box nor the height map of the tub rim / floor): it is
//     just the sagging curve from the mixer to the head. It may pass through the tub or a wall, and
//     it pops to the clamped shape when the head comes back into the box.
//
// ---- CONTACT SENDERS ----
// The water-stream senders are children of the head; their capsules (in their own local space,
// baked by the setup script) are checked against the bathroom box after every move. A capsule that
// would reach outside the bath (through a wall, the door or the floor to 7F) is deactivated, so the
// gimmick never fires for avatars outside the bathroom. ShowerController still switches the whole
// sender root on/off; this only gates the individual capsules.
//
// ---- COST ----
// Runs in PostLateUpdate (after the pickup and VRCObjectSync have placed the head) only while the
// head moves, while it is held locally, or for sleepAfterStill seconds after the last movement,
// then disables itself (enabled = false). ShowerHeadPickup wakes it (pickup events locally, a 4 Hz
// poll on the other clients). No synced state: every client draws from the synced head pose.
[UdonBehaviourSyncMode(BehaviourSyncMode.NoVariableSync)]
public class ShowerHose : UdonSharpBehaviour
{
    [Header("Fixtures")]
    [Tooltip("Fixed point under the mixer. The hose leaves it along the exit direction (startAimPoint / startYawDegrees / startPitchDegrees), not along its own axes.")]
    public Transform hoseStart;
    [Tooltip("Point on the head (bottom of the ferrule). The hose leaves it along -up.")]
    public Transform hoseEnd;
    [Tooltip("Head pickup root.")]
    public Transform headRoot;
    [Tooltip("Water nozzle (ShowerHead), part of the bathroom-box check.")]
    public Transform nozzle;
    public ShowerHeadPickup headPickup;
    public VRCPickup pickup;
    public LineRenderer line;
    [Tooltip("Water particles. Emission is switched off while the head is outside the bathroom box.")]
    public ParticleSystem waterParticles;
    [Tooltip("Water sound (ShowerSound on the head). Told to re-check after the emission switch; optional.")]
    public ShowerSound showerSound;

    [Header("Hose")]
    [Tooltip("Hose length (m) from hoseStart to hoseEnd along the hose. The head is released when pulled further (plus lengthTolerance).")]
    public float hoseLength = 1.2f;
    [Tooltip("Slack allowed before the head is released (m).")]
    public float lengthTolerance = 0.02f;
    [Tooltip("Hose radius (m); the LineRenderer width is twice this.")]
    public float hoseRadius = 0.0075f;
    [Tooltip("Curve samples per half (start->sag point, sag point->end).")]
    public int samplesPerHalf = 14;
    [Tooltip("Direction the slack hose bows to (away from the wall the mixer is on).")]
    public Vector3 pushDirection = new Vector3(0f, 0f, 1f);
    [Tooltip("Bow distance (m) at full slack.")]
    public float pushAmount = 0.18f;
    [Tooltip("Tangent length at the sag point, relative to the half's chord.")]
    public float midTangentScale = 1.0f;
    [Tooltip("Points closer than this to either end are not pushed out of the height map.")]
    public float clampSkipRadius = 0.07f;

    [Header("Exit at the mixer")]
    [Tooltip("The hose leaves the mixer towards this point, horizontally (world; e.g. the middle of the tub). Only the horizontal direction from hoseStart is used.")]
    public Vector3 startAimPoint = new Vector3(0f, 0f, 1f);
    [Tooltip("Turns the exit direction about the vertical from the aim direction (degrees; negative = further west / towards -x when the aim is south).")]
    public float startYawDegrees = -24f;
    [Tooltip("Exit direction below the horizontal (degrees).")]
    public float startPitchDegrees = 35f;

    [Header("Bend")]
    [Tooltip("Segment-direction smoothing passes (1:2:1). More = rounder bends, more cost.")]
    public int bendSmoothIterations = 8;
    [Tooltip("Passes that pull points bent tighter than minBendRadius towards their neighbours (each followed by the height-map clamp).")]
    public int bendRelaxIterations = 3;
    [Tooltip("Target smallest bend radius (m) for the relax passes.")]
    public float minBendRadius = 0.15f;
    [Tooltip("Largest lift (m) per segment when a segment would cut through the height map between its points.")]
    public float segmentLiftMax = 0.04f;
    [Tooltip("Passes that soften points the clamp left bent sharper than despikeAngle (lifted onto a rim) by lifting their neighbours. 0 = off.")]
    public int despikePasses = 4;
    [Tooltip("Bend angle (degrees) between two segments that counts as a spike.")]
    public float despikeAngle = 45f;

    [Header("Bathroom box (world)")]
    [Tooltip("Minimum corner of the bathroom box (world). Outside it the water stops and the hose is not corrected.")]
    public Vector3 roomMin = new Vector3(-1f, 0f, -1f);
    [Tooltip("Maximum corner of the bathroom box (world).")]
    public Vector3 roomMax = new Vector3(1f, 2.5f, 1f);

    [Header("Height map (baked by the setup script)")]
    public float gridOriginX;
    public float gridOriginZ;
    public float cellSize = 0.02f;
    public int gridCountX;
    public int gridCountZ;
    public float[] heights;

    [Header("Contact senders")]
    public GameObject[] senderObjects;
    public Vector3[] senderLocalA;
    public Vector3[] senderLocalB;
    public float[] senderRadius;

    [Header("Sleep")]
    public float sleepAfterStill = 1.0f;

    [Tooltip("Enables [SHOWERHOSE] console logging. Turn OFF before uploading.")]
    public bool debugLog = false;

    private Vector3[] points;
    private Vector3[] raw;       // Hermite samples before re-sampling
    private float[] rawLen;      // cumulative length along raw
    private Vector3[] dirs;      // segment directions for the bend smoothing
    private const int RawHalf = 12;
    private System.Diagnostics.Stopwatch watch;
    private double costSum;
    private int costCount;
    private Vector3 drawnPosition;
    private Quaternion drawnRotation;
    private bool hasDrawn;
    private float lastMoveTime;
    private bool headInside = true;
    private bool emissionApplied = true;

    private void Start()
    {
        int n = samplesPerHalf < 3 ? 3 : samplesPerHalf;
        points = new Vector3[n * 2 + 1];
        raw = new Vector3[RawHalf * 2 + 1];
        rawLen = new float[RawHalf * 2 + 1];
        dirs = new Vector3[n * 2];
        if (debugLog) watch = new System.Diagnostics.Stopwatch();
        if (line != null)
        {
            line.useWorldSpace = true;
            line.positionCount = points.Length;
            line.widthMultiplier = hoseRadius * 2f;
        }
        Redraw();
        lastMoveTime = Time.time;
    }

    // Called by ShowerHeadPickup (pickup events, poll). Cheap; safe to call repeatedly.
    public void _Wake()
    {
        lastMoveTime = Time.time;
        if (!enabled) enabled = true;
    }

    public bool _HeadMovedSinceDraw()
    {
        if (!hasDrawn || headRoot == null) return true;
        return (headRoot.position - drawnPosition).sqrMagnitude > 1e-8f
               || Quaternion.Angle(headRoot.rotation, drawnRotation) > 0.05f;
    }

    public override void PostLateUpdate()
    {
        if (headRoot == null || hoseStart == null || hoseEnd == null) { enabled = false; return; }

        bool held = headPickup != null && headPickup._IsLocallyHeld();
        if (held && ExceedsLimits())
        {
            if (debugLog) Debug.Log("[SHOWERHOSE] limit reached -> drop frame=" + Time.frameCount);
            if (pickup != null) pickup.Drop();
        }

        if (_HeadMovedSinceDraw())
        {
            Redraw();
            lastMoveTime = Time.time;
        }
        else if (!held && Time.time - lastMoveTime > sleepAfterStill)
        {
            enabled = false;
        }
    }

    // The only release condition: the hose is pulled past its length. The bathroom box is never one.
    private bool ExceedsLimits()
    {
        Vector3 s = hoseStart.position;
        Vector3 e = hoseEnd.position;
        return (e - s).magnitude > hoseLength + lengthTolerance;
    }

    // True while the head pivot, the hose end and the nozzle are all inside the bathroom box.
    private bool IsHeadInsideBox()
    {
        if (!InsideBox(headRoot.position, 0f)) return false;
        if (!InsideBox(hoseEnd.position, 0f)) return false;
        if (nozzle != null && !InsideBox(nozzle.position, 0.01f)) return false;
        return true;
    }

    // Water emission follows the head: off while outside the box. Only touched on change.
    private void UpdateEmission()
    {
        if (waterParticles == null || emissionApplied == headInside) return;
        emissionApplied = headInside;
        ParticleSystem.EmissionModule em = waterParticles.emission;
        em.enabled = headInside;
        if (showerSound != null) showerSound._Refresh();
        if (debugLog) Debug.Log("[SHOWERHOSE] water emission " + (headInside ? "on (head back in the bath)" : "off (head outside the bath)"));
    }

    private bool InsideBox(Vector3 p, float margin)
    {
        return p.x >= roomMin.x + margin && p.x <= roomMax.x - margin
            && p.y >= roomMin.y + margin && p.y <= roomMax.y - margin
            && p.z >= roomMin.z + margin && p.z <= roomMax.z - margin;
    }

    private void Redraw()
    {
        drawnPosition = headRoot.position;
        drawnRotation = headRoot.rotation;
        hasDrawn = true;

        headInside = IsHeadInsideBox();
        UpdateEmission();
        UpdateSenders();
        if (line == null || points == null) return;

        if (watch != null) { watch.Reset(); watch.Start(); }
        BuildShape(hoseStart.position, hoseEnd.position, -hoseEnd.up, headInside);
        line.SetPositions(points);
        if (watch != null)
        {
            watch.Stop();
            costSum += watch.Elapsed.TotalMilliseconds;
            costCount++;
            if (costCount >= 60)
            {
                Debug.Log("[SHOWERHOSE] shape cost avg " + (costSum / costCount).ToString("F3") + " ms over " + costCount + " redraws");
                costSum = 0; costCount = 0;
            }
        }
    }

    // Exit direction at the mixer: horizontal direction to startAimPoint, turned by startYawDegrees about
    // the vertical, tilted down by startPitchDegrees.
    public Vector3 _StartDirection()
    {
        Vector3 h = startAimPoint - hoseStart.position;
        h.y = 0f;
        if (h.sqrMagnitude < 1e-8f) h = Vector3.forward;
        h = Quaternion.AngleAxis(startYawDegrees, Vector3.up) * h.normalized;
        float pr = startPitchDegrees * Mathf.Deg2Rad;
        return (h * Mathf.Cos(pr) + Vector3.down * Mathf.Sin(pr)).normalized;
    }

    // Fills points[] (see the header). s = mixer end, e = head end, tE = direction the hose leaves the
    // head (-hoseEnd.up), full = head inside the bathroom box (correct against the box / height map).
    private void BuildShape(Vector3 s, Vector3 e, Vector3 tE, bool full)
    {
        int N = points.Length;
        int last = N - 1;
        float seg = hoseLength / last;
        Vector3 dS = _StartDirection();
        Vector3 s1 = s + dS * seg;
        Vector3 e1 = e + tE * seg;
        float len = hoseLength - 2f * seg;
        if (len < 0.05f) len = 0.05f;
        Vector3 chord = e1 - s1;
        float d = chord.magnitude;
        float taut = d >= len ? 1f : d / len;

        // 1. Sag point: lowest point of the ellipsoid {p : |p-s1| + |p-e1| = len}, pushed off the wall.
        Vector3 c = (s1 + e1) * 0.5f;
        Vector3 m = c;
        if (d < len)
        {
            float a = len * 0.5f;
            float b2 = a * a - d * d * 0.25f;
            Vector3 g = Vector3.down;
            if (d > 1e-5f)
            {
                Vector3 u = chord / d;
                float ug = Vector3.Dot(u, g);
                float hSupport = Mathf.Sqrt(a * a * ug * ug + b2 * (1f - ug * ug));
                if (hSupport > 1e-6f) m = c + (u * (a * a * ug) + (g - u * ug) * b2) / hSupport;
            }
            else
            {
                m = c + g * a;
            }
            m += pushDirection.normalized * (pushAmount * (1f - taut));
        }

        // 2. Tangent at the sag point: horizontal while slack, along the chord when taut.
        Vector3 horiz = new Vector3(chord.x, 0f, chord.z);
        Vector3 chordDir = d > 1e-5f ? chord / d : Vector3.forward;
        Vector3 horizDir = horiz.sqrMagnitude > 1e-8f ? horiz.normalized : chordDir;
        Vector3 tM = Vector3.Lerp(horizDir, chordDir, taut * taut).normalized;
        float k1 = (m - s1).magnitude;
        float k2 = (e1 - m).magnitude;
        Vector3 t0 = dS * k1;
        Vector3 t1 = tM * (k1 * midTangentScale);
        Vector3 t2 = tM * (k2 * midTangentScale);
        Vector3 t3 = -tE * k2; // the hose arrives at the head travelling into it (+up of hoseEnd)
        int rawLast = RawHalf * 2;
        raw[0] = s1;
        rawLen[0] = 0f;
        for (int i = 1; i <= rawLast; i++)
        {
            raw[i] = i <= RawHalf
                ? Hermite(s1, t0, m, t1, (float)i / RawHalf)
                : Hermite(m, t2, e1, t3, (float)(i - RawHalf) / RawHalf);
            rawLen[i] = rawLen[i - 1] + (raw[i] - raw[i - 1]).magnitude;
        }

        // Even spacing between the stubs.
        points[0] = s;
        points[1] = s1;
        points[last - 1] = e1;
        points[last] = e;
        int spans = N - 3;
        float total = rawLen[rawLast];
        int j = 0;
        for (int i = 2; i <= N - 3; i++)
        {
            float target = total * (i - 1) / spans;
            while (j < rawLast - 1 && rawLen[j + 1] < target) j++;
            float span = rawLen[j + 1] - rawLen[j];
            points[i] = Vector3.Lerp(raw[j], raw[j + 1], span > 1e-7f ? (target - rawLen[j]) / span : 0f);
        }

        // 3. Bend smoothing in direction space.
        SmoothBends();

        // 4. Surfaces, then relax the bends that are still too tight, then soften spikes over rims.
        ClampAll(full);
        RelaxBends(full, bendRelaxIterations);
        if (full) Despike();

        // Never hand NaN to the LineRenderer (degenerate input): fall back to a straight hose.
        Vector3 probe = points[N / 2];
        if (probe.x != probe.x || probe.y != probe.y || probe.z != probe.z)
        {
            for (int i = 0; i < N; i++) points[i] = Vector3.Lerp(s, e, (float)i / last);
        }
    }

    // Smooth the free segment directions (1..N-3) with 1:2:1, keep the stub directions and the chord
    // points[1] -> points[N-2]; the segment length stays the curve's own (its drawn length is kept).
    private void SmoothBends()
    {
        int N = points.Length;
        int lastFree = N - 3;
        float total = 0f;
        for (int i = 0; i < N - 1; i++)
        {
            Vector3 v = points[i + 1] - points[i];
            float l = v.magnitude;
            dirs[i] = l > 1e-7f ? v / l : Vector3.down;
            if (i >= 1 && i <= lastFree) total += l;
        }
        if (bendSmoothIterations <= 0) return;
        int count = lastFree;
        float sl = total / count;
        if (sl < 1e-5f) return;
        Vector3 e1 = points[N - 2];
        Vector3 target = (e1 - points[1]) / sl;
        for (int it = 0; it < bendSmoothIterations; it++)
        {
            Vector3 prevOld = dirs[0];
            for (int i = 1; i <= lastFree; i++)
            {
                Vector3 cur = dirs[i];
                dirs[i] = (prevOld + cur * 2f + dirs[i + 1]).normalized;
                prevOld = cur;
            }
            // Re-close the chord: once per pass, twice on the last one.
            int closePasses = it == bendSmoothIterations - 1 ? 2 : 1;
            for (int pass = 0; pass < closePasses; pass++)
            {
                Vector3 sum = Vector3.zero;
                for (int i = 1; i <= lastFree; i++) sum += dirs[i];
                Vector3 err = (target - sum) / count;
                for (int i = 1; i <= lastFree; i++) dirs[i] = (dirs[i] + err).normalized;
            }
        }
        for (int i = 1; i <= lastFree; i++) points[i + 1] = points[i] + dirs[i] * sl;
        // What the closure passes left over goes out along the hose as a straight ramp.
        Vector3 miss = e1 - points[N - 2];
        for (int i = 2; i <= lastFree; i++) points[i] += miss * ((float)(i - 1) / count);
        points[N - 2] = e1;
    }

    // Box + height map for the free points (2..N-3), then lift segments whose thirds cut through it.
    private void ClampAll(bool full)
    {
        if (!full) return;
        int N = points.Length;
        Vector3 s = points[0];
        Vector3 e = points[N - 1];
        float skip2 = clampSkipRadius * clampSkipRadius;
        for (int i = 2; i <= N - 3; i++)
        {
            Vector3 p = points[i];
            if ((p - s).sqrMagnitude < skip2 || (p - e).sqrMagnitude < skip2) continue;
            points[i] = ClampPoint(p, true);
        }
        // Segment midpoints: a free end alone has to rise twice the shortfall to lift the midpoint.
        float r = hoseRadius;
        for (int i = 1; i <= N - 3; i++)
        {
            bool fa = i >= 2;
            bool fb = i + 1 <= N - 3;
            Vector3 q = (points[i] + points[i + 1]) * 0.5f;
            float need = SurfaceHeight(q.x, q.z) + r - q.y;
            if (need <= 0f) continue;
            float lift = (fa && fb) ? need : need * 2f;
            if (lift > segmentLiftMax) lift = segmentLiftMax;
            if (fa) { Vector3 pa = points[i]; pa.y += lift; points[i] = pa; }
            if (fb) { Vector3 pb = points[i + 1]; pb.y += lift; points[i + 1] = pb; }
        }
    }

    // Pull points bent tighter than minBendRadius (three-point circle) towards their neighbours' midpoint.
    private void RelaxBends(bool full, int iterations)
    {
        int N = points.Length;
        float R = minBendRadius;
        for (int it = 0; it < iterations; it++)
        {
            for (int i = 2; i <= N - 3; i++)
            {
                Vector3 a = points[i - 1];
                Vector3 b = points[i + 1];
                Vector3 mid = (a + b) * 0.5f;
                Vector3 off = points[i] - mid;
                float h = off.magnitude;
                if (h < 1e-7f) continue;
                float c = (b - a).magnitude * 0.5f;
                float hmax = c < R ? R - Mathf.Sqrt(R * R - c * c) : c;
                if (h > hmax) points[i] = mid + off * (hmax / h);
            }
            ClampAll(full);
        }
    }

    // Where the clamp left a point bent sharply (more than despikeAngle) — typically a single point lifted
    // onto the tub rim or the basin while its neighbours hang below — its lower neighbours are lifted
    // halfway towards it, then clamp + one relax pass. Stops early when nothing is bent that sharply.
    private void Despike()
    {
        int N = points.Length;
        float cosLim = Mathf.Cos(despikeAngle * Mathf.Deg2Rad);
        for (int pass = 0; pass < despikePasses; pass++)
        {
            bool any = false;
            for (int i = 2; i <= N - 3; i++)
            {
                Vector3 a = points[i] - points[i - 1];
                Vector3 b = points[i + 1] - points[i];
                float lab = a.magnitude * b.magnitude;
                if (lab < 1e-10f || Vector3.Dot(a, b) > cosLim * lab) continue;
                float y = points[i].y;
                if (i - 1 >= 2 && points[i - 1].y < y) { Vector3 pa = points[i - 1]; pa.y = (pa.y + y) * 0.5f; points[i - 1] = pa; any = true; }
                if (i + 1 <= N - 3 && points[i + 1].y < y) { Vector3 pb = points[i + 1]; pb.y = (pb.y + y) * 0.5f; points[i + 1] = pb; any = true; }
            }
            if (!any) break;
            ClampAll(true);
            RelaxBends(true, 1);
        }
    }

    // full = head inside the box: keep the point inside the box and above the height map.
    // Not full = head outside the box: no correction at all (the hose is just the sagging curve; the
    // height map would lift it onto the tub wall where it leaves the bath and make it zigzag).
    private Vector3 ClampPoint(Vector3 p, bool full)
    {
        if (!full) return p;
        float r = hoseRadius;
        p.x = Mathf.Clamp(p.x, roomMin.x + r, roomMax.x - r);
        p.z = Mathf.Clamp(p.z, roomMin.z + r, roomMax.z - r);
        float floorY = SurfaceHeight(p.x, p.z) + r;
        if (p.y < floorY) p.y = floorY;
        if (p.y > roomMax.y - r) p.y = roomMax.y - r;
        return p;
    }

    private float SurfaceHeight(float x, float z)
    {
        if (heights == null || gridCountX <= 0 || gridCountZ <= 0 || heights.Length < gridCountX * gridCountZ) return roomMin.y;
        int ix = Mathf.Clamp(Mathf.FloorToInt((x - gridOriginX) / cellSize), 0, gridCountX - 1);
        int iz = Mathf.Clamp(Mathf.FloorToInt((z - gridOriginZ) / cellSize), 0, gridCountZ - 1);
        return heights[iz * gridCountX + ix];
    }

    private Vector3 Hermite(Vector3 p0, Vector3 m0, Vector3 p1, Vector3 m1, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return p0 * (2f * t3 - 3f * t2 + 1f)
             + m0 * (t3 - 2f * t2 + t)
             + p1 * (-2f * t3 + 3f * t2)
             + m1 * (t3 - t2);
    }

    // Deactivate any sender capsule that would reach outside the bathroom box.
    private void UpdateSenders()
    {
        if (senderObjects == null) return;
        for (int i = 0; i < senderObjects.Length; i++)
        {
            GameObject go = senderObjects[i];
            if (go == null || senderLocalA == null || senderLocalB == null || senderRadius == null) continue;
            if (i >= senderLocalA.Length || i >= senderLocalB.Length || i >= senderRadius.Length) continue;
            Transform t = go.transform;
            float r = senderRadius[i];
            // Head outside the box: every sender is off, whatever its capsule says.
            bool inside = headInside && InsideBox(t.TransformPoint(senderLocalA[i]), r) && InsideBox(t.TransformPoint(senderLocalB[i]), r);
            if (go.activeSelf != inside)
            {
                go.SetActive(inside);
                if (debugLog) Debug.Log("[SHOWERHOSE] sender " + go.name + (inside ? " back inside -> on" : " would leave the bath -> off"));
            }
        }
    }
}
