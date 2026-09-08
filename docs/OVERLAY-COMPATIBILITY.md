# Gaming on a second monitor

The user clarified on 2026-09-07: **"Keep it on another monitor while I play."**

The required product is a normal desktop widget on a separate monitor while iRacing, Assetto Corsa Competizione, Call of Duty, or another game runs on the game monitor. This replaces the earlier same-screen exclusive-fullscreen overlay requirement. No game injection, rendering hooks, or Game Bar widget are needed for the clarified scope.

The widget reads system hardware counters through the local sensor service. It does not inspect game memory, alter game files, inject code into a game, or change anti-cheat/security configuration. The same seven readings, layout, transparency, position lock, and programmable show/hide shortcut are available while gaming. Game compatibility still needs pilot verification on real PCs; no title-specific test result should be inferred from a successful desktop test.

## Pilot checks

| Game | Required display arrangement | Status |
| --- | --- | --- |
| iRacing | Game on primary display; DLB on another display | Pending real game test |
| Assetto Corsa Competizione | Game on primary display; DLB on another display | Pending real game test |
| Call of Duty | Game on primary display; DLB on another display | Pending exact title/build test |
| Other mainstream games | Game on primary display; DLB on another display | Per-title pilot verification |

Record Windows build, CPU/GPU and driver, game build, both displays' resolutions and scaling, and whether normal anti-cheat/security settings remain enabled. Check retained position when a game changes resolution; show/hide shortcut without stealing game focus; GPU selection; sleep/resume; monitor disconnect/reconnect; and aggregate CPU, RAM and frame pacing for the widget plus sensor service.

Fullscreen games can change a display's resolution and affect desktop coordinates. The app should keep the window visible after monitor changes. If Windows changes the available desktop arrangement, restoring to a visible display is preferable to leaving the widget offscreen.

## Earlier research, retained for context

A normal topmost window is not a universal overlay above true exclusive fullscreen. Windows Fullscreen Optimizations can also make a game's fullscreen setting behave as optimized borderless. This distinction matters only if same-screen game rendering is requested again. [Microsoft: Demystifying Fullscreen Optimizations](https://devblogs.microsoft.com/directx/demystifying-full-screen-optimizations/)

Microsoft's Game Bar SDK offers a supported widget host, but documents fullscreen Vulkan/OpenGL limitations. It adds a separate package, platform dependency, and resource cost; it is not part of this second-monitor build. [Microsoft: Game Bar overview](https://learn.microsoft.com/en-us/gaming/game-bar/overview), [known issues](https://learn.microsoft.com/en-us/xbox/game-bar/known-issues)

Anti-cheat policies are maintained by game publishers. Activision describes monitoring applications during gameplay; absence of injection is a design property, not a blanket certification by a game publisher. Verify the priority games during the private pilot using their normal protections. [Activision: Security and Enforcement Policy](https://support.activision.com/articles/call-of-duty-security-and-enforcement-policy)
