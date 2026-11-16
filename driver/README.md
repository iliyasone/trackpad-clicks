# Magic Trackpad driver

The precision-touchpad driver for the Apple Magic Trackpad that `Install.cs` installs,
taken unchanged from [MagicTrackpad2ForWindows v2.3](https://github.com/vitoplantamura/MagicTrackpad2ForWindows/releases/tag/v2.3)
(Vito Plantamura, Bingxing Wang; source at commit
[3392291](https://github.com/vitoplantamura/MagicTrackpad2ForWindows/tree/3392291af4814efb33d0d2b8b99948eca33a77a1);
GPLv2, see `LICENSE`). TrackpadClicks embeds these files in its exe.

- `win11/` — `MT2FW11-amd64-20261111-MSSigned.zip`: catalog signed by Microsoft for
  Windows 11 22H2 and later.
- `win10/` — `MT2FW10-amd64-20261111-SelfSigned.zip`, for Windows 10 and Windows 11 21H2: the same driver files (signed by
  Microsoft), with a catalog signed by `MagicTrackpad2ForWindows.cer`, which must be
  trusted (Trusted Root and Trusted Publishers) before the package installs.

Neither has the edge-zone hold (`IOCTL_PTPFILTER_HOLD`); that is only in the
[iliyasone fork](https://github.com/iliyasone/MagicTrackpad2ForWindows/tree/edge-hold),
built test-signed.
