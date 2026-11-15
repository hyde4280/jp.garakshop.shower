# Changelog

All notable changes to this package will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [0.1.1]

### Fixed

- The water stream no longer looks like it starts behind the shower head: the water particle emitter is moved 0.03 m forward (the stretched streaks used to extend behind the plate).
- README: installation steps and package contents now match the published package.

## [0.1.0]

### Added

- Package skeleton: `package.json`, README, LICENSE, and the runtime assembly definitions.
- Shower scripts, meshes, materials, sound and prefab, and the generator tools (`Tools~/`).
- Sample scene `Samples~/ShowerSample` (import it from the Package Manager Samples section).
- The scripts are in the `GarakShop.Shower` namespace; use `using GarakShop.Shower;` to extend them (for example `ShowerRippleReceiver`).
