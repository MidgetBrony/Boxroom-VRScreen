# Boxroom-VRScreen 0.3.35

**Experimental · Windows only**

- Binds the helper process to BOXROOM immediately at startup.
- Stops the helper automatically when the owning BOXROOM process exits.
- Adds graceful helper shutdown when the mod unloads, with a forced fallback.
- Clears stale launch requests before starting a new helper session.
