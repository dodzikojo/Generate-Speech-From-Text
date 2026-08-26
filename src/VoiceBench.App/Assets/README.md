# Generated placeholder branding

VoiceBench does not check temporary bitmap branding into source control. The build runs
`Build/GeneratePlaceholderAssets.ps1` and places deterministic placeholder PNGs beneath the
project's intermediate output directory before MSIX packaging begins.

Replace that generator with final IssueLab artwork before public distribution. Keep the same
manifest paths and required dimensions so packaging remains reproducible.
