# Garak's Shop Shower

A shower gimmick for VRChat worlds, written in UdonSharp.

- Grab the shower head (VRC Pickup) and take it off the holder
- A hose that follows the head
- Water splash particles
- Running-water sound

## Requirements

- Unity 2022.3
- VRChat Worlds SDK (`com.vrchat.worlds`) 3.10.5 or later (3.x)

## Installation

1. Open VRChat Creator Companion (VCC).
2. Add the repository listing: open <https://hyde4280.github.io/garakshop-vpm/> in a browser and press **Add to VCC**, or in the VCC use Settings > Packages > Add Repository and paste `https://hyde4280.github.io/garakshop-vpm/index.json`.
3. Add **Garak's Shop Shower** to your world project.

## Contents

```
jp.garakshop.shower/
  package.json
  README.md
  CHANGELOG.md
  LICENSE
  Runtime/
    GarakShop.Shower.asmdef   Assembly definition for the scripts
    GarakShop.Shower.asset    UdonSharp assembly definition
    Scripts/                  UdonSharp scripts
    Meshes/                   Shower head, holder and mixer meshes
    Materials/                Materials
    Prefabs/                  ShowerKit prefab
    Audio/                    Sound clips
  Samples~/ShowerSample/      Sample scene (import from the Package Manager)
  Tools~/                     Blender / Python scripts that generate the meshes and the sound
```

## Usage

All scripts are in the `GarakShop.Shower` namespace. A script of your own that uses or extends them (for example a subclass of `ShowerRippleReceiver`) needs `using GarakShop.Shower;`, and its assembly must reference `GarakShop.Shower`.

## Sample

Import **Shower Sample** from the package's *Samples* section (Unity Package Manager window, or the package's entry in the Window > Package Manager list), then open `ShowerSample.unity`. It has a floor, a wall, a `VRCSceneDescriptor` with one spawn, and one `ShowerKit` prefab at the world origin (the shower coordinates are world coordinates, so keep the prefab root at the origin and unrotated). `ShowerSplash.ripples` is left empty, so there is no bath water surface. The scene has no lighting bake or skybox; it only checks that the shower works.

## License

MIT License. See [LICENSE](LICENSE).
