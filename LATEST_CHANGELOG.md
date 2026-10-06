## v1.4.2 (patch)

Changes since v1.4.1:

- [patch] Run queued Invoke/InvokeAsync work on the default scheduler, so it stays on the owner thread ([@matt-edmondson](https://github.com/matt-edmondson))
- [patch] Reject a TryBeginInvoke capacity above 2^30 with ArgumentOutOfRangeException ([@matt-edmondson](https://github.com/matt-edmondson))
- Wait for the task an async delegate returns in Invoke/InvokeAsync ([@matt-edmondson](https://github.com/matt-edmondson))
- Keep the DoInvokes comments next to the code they describe ([@matt-edmondson](https://github.com/matt-edmondson))
- Run only the work queued when DoInvokes starts ([@matt-edmondson](https://github.com/matt-edmondson))

