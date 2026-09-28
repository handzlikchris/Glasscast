package uk.co.reliablesolutions.glassesremote.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class RecentAppsTest {
    private fun apps(vararg newestFirst: String) = RecentApps().apply { newestFirst.reversed().forEach { used(it) } }

    @Test
    fun previous_goes_back_and_quick_steps_keep_going() {
        val r = apps("chrome", "claude", "maps")
        assertEquals("claude", r.step(previous = true, now = 0))
        // The switch brings claude to the front; the cycle's list stays as it was.
        r.used("claude")
        assertEquals("maps", r.step(previous = true, now = 500))
        r.used("maps")
        assertEquals("chrome", r.step(previous = true, now = 1000))
    }

    @Test
    fun next_goes_forward_and_wraps() {
        val r = apps("chrome", "claude", "maps")
        assertEquals("maps", r.step(previous = false, now = 0))
        assertEquals("claude", r.step(previous = false, now = 100))
        assertEquals("maps", r.step(previous = true, now = 200))
    }

    @Test
    fun after_a_pause_a_step_starts_from_the_app_in_front() {
        val r = apps("chrome", "claude")
        assertEquals("claude", r.step(previous = true, now = 0))
        r.used("claude")
        // Four seconds later: previous from claude is chrome again (a toggle, like Alt+Tab).
        assertEquals("chrome", r.step(previous = true, now = 4000))
    }

    @Test
    fun needs_two_apps() {
        assertNull(apps("chrome").step(previous = true, now = 0))
        assertNull(RecentApps().step(previous = false, now = 0))
    }

    @Test
    fun keeps_a_short_list_without_repeats() {
        val r = RecentApps(maxApps = 3)
        listOf("a", "b", "a", "c", "d").forEach { r.used(it) }
        assertEquals(listOf("d", "c", "a"), r.snapshot())
    }
}
