package uk.co.reliablesolutions.glassesremote.companion

/**
 * Apps used on the phone, newest first, and stepping through them like Alt+Tab. The accessibility
 * service feeds it (an app comes to the front → [used]); the glasses' "previous"/"next" app
 * swipes [step] through it. Only apps seen here can be switched to: the glasses never name one.
 *
 * Quick steps (within [cycleMs] of each other) walk one list, taken at the first step, so the
 * switches themselves don't reorder it: previous, previous goes two apps back. After a pause the
 * next step starts again from the app in front.
 */
class RecentApps(private val maxApps: Int = 12, private val cycleMs: Long = 3000) {
    private val recent = ArrayList<String>()
    private var cycle: List<String>? = null
    private var cursor = 0
    private var lastStepAt = Long.MIN_VALUE / 2

    /** [pkg] came to the front. */
    @Synchronized
    fun used(pkg: String) {
        recent.remove(pkg)
        recent.add(0, pkg)
        while (recent.size > maxApps) recent.removeAt(recent.size - 1)
    }

    @Synchronized
    fun snapshot(): List<String> = ArrayList(recent)

    /**
     * The app to switch to: [previous] = one further back (older), else one forward (newer),
     * wrapping round. Null with fewer than two apps known.
     */
    @Synchronized
    fun step(previous: Boolean, now: Long): String? {
        val continuing = cycle != null && now - lastStepAt < cycleMs
        val list = if (continuing) cycle!! else ArrayList(recent)
        if (!continuing) {
            cycle = list
            cursor = 0
        }
        if (list.size < 2) return null
        cursor = (cursor + (if (previous) 1 else -1) + list.size) % list.size
        lastStepAt = now
        return list[cursor]
    }
}
