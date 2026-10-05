package uk.co.reliablesolutions.glassesremote.companion

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class ConnectCodeTest {
    @Test
    fun typedCodesBecomeTheServersShape() {
        assertEquals("ABC-234", ConnectCode.normalize("ABC-234"))
        assertEquals("ABC-234", ConnectCode.normalize("abc 234"))
        assertEquals("ABC-234", ConnectCode.normalize(" abc234 "))
    }

    @Test
    fun anythingElseIsNotACode() {
        assertNull(ConnectCode.normalize("ABC-23"))
        assertNull(ConnectCode.normalize("ABC-2345"))
        assertNull(ConnectCode.normalize("ABO-234"))
        assertNull(ConnectCode.normalize("AB1-234"))
        assertNull(ConnectCode.normalize("ABC_234"))
        assertNull(ConnectCode.normalize(""))
    }
}
