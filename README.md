# Sensory Studio

Sensory Studio is an exploratory sample created by the **Human Centered Design team in Windows & Devices**. Inspired by inclusive design research and conversations with neurodivergent people, it uses digital fidgeting layered with tactile feedback to explore how haptics can support different sensory preferences and make digital experiences more inclusive and engaging.

## Why we're exploring

For some people, fidgeting can help maintain focus, manage sensory input, or provide a sense of grounding. Physical fidget tools aren't always convenient to carry or use in every setting.

Sensory Studio explores a complementary possibility: discreet sensory experiences available on the device you're already using. By bringing haptics into digital interactions, we're investigating how technology might better accommodate a wider range of preferences.

## Explore the experiences

### Zen Garden

Rake virtual sand, move stones, and arrange your garden. Visuals, sound, and haptic feedback bring a sense of texture and physical interaction to a digital space.

### Power Orb

Glide over, press, and squeeze a floating particle orb. Explore how movement and haptic feedback can make a simple digital interaction feel tactile and responsive.

Select **Pop out mode** <img src="docs/pop-out-icon.png" alt="Pop out mode icon" width="20" height="20"> to keep the orb in a separate transparent window for digital fidgeting during meetings or other focused tasks.

## What you'll need

A Windows PC and, for the full experience, **compatible haptic hardware**, such as:

- Logitech MX Master 4
- Surface Mouse 2nd Edition
- Another device that supports advanced haptics on Windows

Haptic feedback also requires a compatible Windows version. Without haptic hardware, you can still explore the visuals and available sound.

## Get started

This repository contains source code, not a ready-to-install app.

Follow the **[setup guide](docs/SetupGuide.md)** to download the project, build it for your PC, and find the app to open. The guide also includes troubleshooting for common setup issues.

The setup steps restore packages from NuGet.org's official v2 HTTPS endpoint, then build with `--no-restore`, avoiding the v3 connection failure reported on some networks. TLS verification stays enabled; see the guide's [package vulnerability checks](docs/SetupGuide.md#package-vulnerability-checks) for the auditing caveat.

## About this sample

Sensory Studio is an exploration, not a production-ready or officially supported product. It is provided as-is, and experiences will vary by person and device.

**License:** The original sample's source license is pending; no open-source license has been granted yet. Third-party components retain their respective terms - see [Third-party notices](THIRD-PARTY-NOTICES.txt).
