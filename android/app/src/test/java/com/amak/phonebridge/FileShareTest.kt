package com.amak.phonebridge

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FileShareTest {
    @Test fun keepsOrdinaryNames() {
        assertEquals("Holiday photo.jpg", FileShare.safeName("Holiday photo.jpg"))
    }

    @Test fun dropsFoldersAndTraversal() {
        assertEquals("passwd", FileShare.safeName("../../etc/passwd"))
        assertEquals("evil.exe", FileShare.safeName("C:\\Windows\\System32\\evil.exe"))
        assertEquals("file", FileShare.safeName(".."))
        assertEquals("file", FileShare.safeName(""))
    }

    @Test fun removesReservedAndControlCharacters() {
        val n = FileShare.safeName("a<b>c:d\"e|f?g*h\u0000i\n.txt")
        assertEquals("abcdefghi.txt", n)
    }

    @Test fun trimsTrailingDotsAndSpaces() {
        assertEquals("notes", FileShare.safeName("notes. . "))
    }

    @Test fun shortensLongNamesButKeepsTheExtension() {
        val n = FileShare.safeName("x".repeat(300) + ".pdf")
        assertTrue(n.length <= 120)
        assertTrue(n.endsWith(".pdf"))
        assertFalse(n.contains('/'))
    }
}
