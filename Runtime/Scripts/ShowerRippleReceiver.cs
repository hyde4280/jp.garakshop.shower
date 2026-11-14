using UdonSharp;

// Receiver of the "where the shower stream meets the water" points sent by ShowerSplash (added
// 2026-11-14, Issue #77). Part of the shower kit (jp.garakshop.shower); the water surface that makes
// the ripples is NOT part of the kit: a world script derives from this class (in this world
// BathWaterRipples, the 801 tub) and ShowerSplash.ripples points at it. Leave ShowerSplash.ripples
// empty when there is no water to ripple.
//
// WHY A BASE CLASS (and not SendCustomEvent / SetProgramVariable on a plain UdonBehaviour): U# has no
// interfaces, and a call with arguments through a plain UdonBehaviour needs fixed variable names on
// the receiver (new public fields). An abstract UdonSharpBehaviour keeps the calls typed and checked
// at compile time, adds no serialized fields, and compiles to the same SendCustomEvent with the same
// method names as before, so the existing scene reference stays valid as it is.
//
// Protocol (local, on the client that computed the points; nothing is synced):
//   once per ShowerSplash geometry check while the tub is "active":
//     _SetShowerPoint(0..n-1, x, z, weight)   world x / z of each point, weight 0..1
//                                              (1 = the stream itself, less = a splash off a body)
//     _SetShowerCount(n)                       n points are valid now (0 = none)
//   _SetShowerCount(0) when the water stops.
// ShowerSplash also treats the water as present only while this component's GameObject is active
// in the hierarchy (e.g. the tub is filled); when inactive, the stream passes through the water plane.
// The leading underscore keeps the methods off the network surface.
public abstract class ShowerRippleReceiver : UdonSharpBehaviour
{
    public abstract void _SetShowerPoint(int i, float x, float z, float weight);

    public abstract void _SetShowerCount(int n);
}
