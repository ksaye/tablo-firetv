# Changelog

## 1.0.0 — 2026-09-17

First public release.

- Live TV (antenna and free streaming channels), recordings and a 14-day guide, all from the remote.
- Talks to the DVR directly: an in-app server answers the bundled tablo-web interface, and a native
  player plays the DVR's MPEG-2/AC3 streams in hardware — no server, no transcoding.
- Live TV: channel up/down (Center tunes a pending change at once), pause, rewind and fast-forward.
- Recordings play from a VOD copy of the DVR's playlist, so they start at the beginning and seek.
- Sign-in remembered in Android's encrypted storage; the guide is kept on the device and reloaded at
  most once a day.
- Remote navigation: a straight-ahead target now always wins over a nearer diagonal one, so Left and
  Right travel along the header.
- Updates itself from GitHub releases.
