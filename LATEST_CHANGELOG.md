## v1.5.0 (minor)

Changes since v1.4.0:

- Deny child attachment to the tasks Invoke queues [patch] (#93) (matt-edmondson)
- Regenerate CompatibilitySuppressions.xml after the Polyfill 11.4.5 bump [patch] (#89) (matt-edmondson)
- Report every TryBeginInvoke failure when a BeginInvokeFailed handler throws [patch] (#88) (matt-edmondson)
- Wait for ValueTask-returning delegates in Invoke and InvokeAsync [minor] (#87) (matt-edmondson)
- Throw ArgumentNullException from InvokeAsync at the call site [patch] (Claude)
- Run InvokeAsync and TryBeginInvoke work in submission order [patch] (Claude)
- [patch] Run queued Invoke/InvokeAsync work on the default scheduler, so it stays on the owner thread (matt-edmondson)
- [patch] Reject a TryBeginInvoke capacity above 2^30 with ArgumentOutOfRangeException (matt-edmondson)
- Wait for the task an async delegate returns in Invoke/InvokeAsync (matt-edmondson)
- Keep the DoInvokes comments next to the code they describe (matt-edmondson)
- Run only the work queued when DoInvokes starts (matt-edmondson)
- Move CI onto the shared ci-shared.yml pipeline (Claude)

