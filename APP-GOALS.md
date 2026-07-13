# ARK Join Assist — product goals

## Purpose

Make it faster and easier to enter a full ARK: Survival Ascended server by automating the repetitive in-game menu controls a player would otherwise click manually.

The user chooses the desired server in ASA. After that, the helper uses ASA's own last-played server and should require only one action in the helper: click **GO**.

## Desired experience

- No server address, API key, console command, calibration, or app-side server selection.
- One-click start, an obvious **STOP** control, and a global **Ctrl+G** shortcut that toggles start/stop.
- Use the shortest available path through ASA's current menus.
- Retry as soon as ASA's interface is ready rather than waiting an arbitrary fixed interval.
- Allow an optional user-selected minimum spacing between join attempts, defaulting to zero.
- Continue watching unresolved join attempts indefinitely because ASA can take more than a minute to return a timeout.
- Automatically dismiss known retryable outcomes, including full-server and connection-timeout dialogs.
- Keep the activity log visible while running.
- Play a one-time attention sound after five seconds without a recognized transition so possible loading or an unhandled state is noticed promptly.
- Show attempt count and the running average time per attempt.
- Show attempt rate for both the current run and the full app session, and retain temporary visual evidence only for positively recognized error dialogs and confirmed globe-loading states until the app closes.
- Adapt conservatively when ASA changes its UI.

## Core workflow

1. Detect ASA's current visible menu state.
2. Use an available **Join Last Session** or **Join Last Played** control.
3. When a retryable failure appears, dismiss it using the appropriate visible control.
4. Navigate back through ASA's menus only as far as necessary to make another attempt.
5. Repeat until joining or loading appears to continue.
6. Stop sending input whenever the screen is unknown or a successful connection may be underway.

## Product principles

- Prefer observed in-game behavior over assumptions about ASA's menus.
- Recognize screen states before clicking; do not rely solely on fixed delays or coordinates.
- Map recognition and clicks through ASA's centered safe UI area so aspect-ratio changes do not shift controls.
- Treat coordinates as state-specific implementation details because ASA's UI changes over time.
- Optimize the complete time between real join attempts, not just the click rate.
- Make ordinary use possible without setup instructions or technical knowledge.
- Keep the interface compact, legible, and focused on GO, STOP, and live activity.

## Safety boundaries

The helper is limited to visible ASA startup and multiplayer-menu controls. It must not automate gameplay, inspect or modify game memory, inject code, manipulate network traffic, alter BattlEye or game files, or embed private service credentials.

Unknown screens, contradictory signals, capture failures, or loss of the ASA process must stop or pause input safely. Once joining, loading, or gameplay may have begun, the helper must not continue clicking.
