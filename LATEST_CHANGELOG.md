## v1.6.0 (minor)

Changes since v1.5.0:

- Release blocked Invoke callers with a cancellation token or Shutdown [minor] (#112) (matt-edmondson)
- Queue an owner-thread TryBeginInvoke for the next pump while DoInvokes runs [patch] (#114) (matt-edmondson)
- Report an owner-thread TryBeginInvoke failure instead of throwing it to the caller [patch] (#99) (matt-edmondson)

