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

> **Not published yet.** The VPM listing below is planned and does not serve this package yet.

1. Open VRChat Creator Companion (VCC).
2. Add the repository listing: `https://hyde4280.github.io/garakshop-vpm/index.json`
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
    Audio/                    Sound clips
```

Meshes, materials and the prefab are added as the package is filled in.

## Usage

All scripts are in the `GarakShop.Shower` namespace. A script of your own that uses or extends them (for example a subclass of `ShowerRippleReceiver`) needs `using GarakShop.Shower;`, and its assembly must reference `GarakShop.Shower`.

## License

MIT License. See [LICENSE](LICENSE).
