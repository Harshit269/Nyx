package com.amak.phonebridge

import android.animation.ValueAnimator
import android.content.Context
import android.graphics.Canvas
import android.graphics.Paint
import android.graphics.RectF
import android.view.View
import android.view.animation.DecelerateInterpolator

/**
 * Text drawn as a 5x7 dot matrix, the same glyphs as the laptop app. When the text
 * changes the dots light up in a quick diagonal sweep.
 */
class DotTextView(c: Context, private val dotDp: Float) : View(c) {
    var text: String = ""
        set(v) {
            val up = v.uppercase()
            if (up == field) return
            field = up
            requestLayout()
            sweep()
        }
    var color: Int = 0xFFFFFFFF.toInt()
        set(v) { field = v; paint.color = v; invalidate() }
    /** Unlit dots of each cell, like a real LED panel; 0 = hidden. */
    var offColor: Int = 0
        set(v) { field = v; invalidate() }

    private val dot = dotDp * resources.displayMetrics.density
    private val pitch = dot * 1.5f
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val off = Paint(Paint.ANTI_ALIAS_FLAG)
    private var reveal = 1f
    private var anim: ValueAnimator? = null

    private fun cellWidth(ch: Char) = if (ch == ' ') 3 else if (ch == ':' || ch == '.') 2 else 5

    private fun cols(): Int {
        var n = 0
        text.forEachIndexed { i, ch -> n += cellWidth(ch) + if (i < text.length - 1) 1 else 0 }
        return n
    }

    override fun onMeasure(w: Int, h: Int) {
        setMeasuredDimension((cols() * pitch - (pitch - dot)).toInt().coerceAtLeast(0) + 1, (7 * pitch - (pitch - dot)).toInt() + 1)
    }

    private fun sweep() {
        if (!isAttachedToWindow) { reveal = 1f; invalidate(); return }
        anim?.cancel()
        anim = ValueAnimator.ofFloat(0f, 1f).apply {
            duration = 560
            interpolator = DecelerateInterpolator(1.6f)
            addUpdateListener { reveal = it.animatedValue as Float; invalidate() }
            start()
        }
    }

    override fun onDraw(canvas: Canvas) {
        val r = dot / 2
        val total = (width + height).coerceAtLeast(1).toFloat()
        off.color = offColor
        var x0 = 0f
        for (raw in text) {
            val ch = if (GLYPHS.containsKey(raw)) raw else ' '
            val w = cellWidth(ch)
            val rows = GLYPHS.getValue(ch)
            for (y in 0 until 7) for (x in 0 until w) {
                val src = if (w == 2) x + 1 else x
                val on = src < rows[y].length && rows[y][src] == '1'
                val cx = x0 + x * pitch + r
                val cy = y * pitch + r
                if (on && (cx + cy) / total <= reveal * 1.02f) canvas.drawCircle(cx, cy, r, paint)
                else if (offColor != 0) canvas.drawCircle(cx, cy, r, off)
            }
            x0 += (w + 1) * pitch
        }
    }

    companion object {
        private val GLYPHS: Map<Char, List<String>> = buildMap {
            put(' ', List(7) { "000" })
            """
A 01110 10001 10001 11111 10001 10001 10001
B 11110 10001 10001 11110 10001 10001 11110
C 01110 10001 10000 10000 10000 10001 01110
D 11110 10001 10001 10001 10001 10001 11110
E 11111 10000 10000 11110 10000 10000 11111
F 11111 10000 10000 11110 10000 10000 10000
G 01110 10001 10000 10111 10001 10001 01111
H 10001 10001 10001 11111 10001 10001 10001
I 01110 00100 00100 00100 00100 00100 01110
J 00111 00010 00010 00010 00010 10010 01100
K 10001 10010 10100 11000 10100 10010 10001
L 10000 10000 10000 10000 10000 10000 11111
M 10001 11011 10101 10101 10001 10001 10001
N 10001 10001 11001 10101 10011 10001 10001
O 01110 10001 10001 10001 10001 10001 01110
P 11110 10001 10001 11110 10000 10000 10000
Q 01110 10001 10001 10001 10101 10010 01101
R 11110 10001 10001 11110 10100 10010 10001
S 01111 10000 10000 01110 00001 00001 11110
T 11111 00100 00100 00100 00100 00100 00100
U 10001 10001 10001 10001 10001 10001 01110
V 10001 10001 10001 10001 10001 01010 00100
W 10001 10001 10001 10101 10101 10101 01010
X 10001 10001 01010 00100 01010 10001 10001
Y 10001 10001 01010 00100 00100 00100 00100
Z 11111 00001 00010 00100 01000 10000 11111
0 01110 10001 10011 10101 11001 10001 01110
1 00100 01100 00100 00100 00100 00100 01110
2 01110 10001 00001 00010 00100 01000 11111
3 11111 00010 00100 00010 00001 10001 01110
4 00010 00110 01010 10010 11111 00010 00010
5 11111 10000 11110 00001 00001 10001 01110
6 00110 01000 10000 11110 10001 10001 01110
7 11111 00001 00010 00100 01000 01000 01000
8 01110 10001 10001 01110 10001 10001 01110
9 01110 10001 10001 01111 00001 00010 01100
: 00000 01100 01100 00000 01100 01100 00000
. 00000 00000 00000 00000 00000 01100 01100
- 00000 00000 00000 11111 00000 00000 00000
% 11000 11001 00010 00100 01000 10011 00011
""".trim().lines().forEach { line ->
                val parts = line.trim().split(' ').filter { it.isNotEmpty() }
                put(parts[0][0], parts.drop(1))
            }
        }
    }
}

/** A status dot; while [pulsing] it breathes slowly, like a recording light. */
class PulseDot(c: Context) : View(c) {
    private val paint = Paint(Paint.ANTI_ALIAS_FLAG)
    private val halo = Paint(Paint.ANTI_ALIAS_FLAG)
    private var phase = 0f
    private val anim = ValueAnimator.ofFloat(0f, 1f).apply {
        duration = 1600
        repeatCount = ValueAnimator.INFINITE
        addUpdateListener { phase = it.animatedValue as Float; invalidate() }
    }
    var color: Int = 0xFFEB2F37.toInt()
        set(v) { field = v; paint.color = v; halo.color = v; invalidate() }
    var pulsing = false
        set(v) {
            if (field == v) return
            field = v
            if (v && isAttachedToWindow) anim.start() else { anim.cancel(); phase = 0f; invalidate() }
        }

    init { color = color }

    override fun onAttachedToWindow() { super.onAttachedToWindow(); if (pulsing) anim.start() }
    override fun onDetachedFromWindow() { anim.cancel(); super.onDetachedFromWindow() }

    override fun onDraw(canvas: Canvas) {
        val cx = width / 2f
        val cy = height / 2f
        val r = minOf(width, height) / 4f
        if (pulsing) {
            halo.alpha = ((1f - phase) * 90).toInt()
            canvas.drawCircle(cx, cy, r * (1f + phase), halo)
        }
        canvas.drawCircle(cx, cy, r, paint)
    }
}

/**
 * The laptop app's switch: a pill with a round knob; when on, the track is ink and
 * the knob carries a small red dot. Slides between states.
 */
class PillSwitch(c: Context) : View(c) {
    var ink = 0xFFFFFFFF.toInt()
    var track = 0xFF2A2A2A.toInt()
    var canvasColor = 0xFF000000.toInt()
    var red = 0xFFEB2F37.toInt()
    var onToggle: ((Boolean) -> Unit)? = null

    var checked = false
        private set
    private var pos = 0f
    private val p = Paint(Paint.ANTI_ALIAS_FLAG)
    private val rect = RectF()

    init {
        isClickable = true
        isFocusable = true
        setOnClickListener { onToggle?.invoke(!checked) }
    }

    fun set(on: Boolean, animate: Boolean = true) {
        if (on == checked && !(pos != (if (on) 1f else 0f))) return
        checked = on
        contentDescription = if (on) "On" else "Off"
        val to = if (on) 1f else 0f
        if (!animate || !isAttachedToWindow) { pos = to; invalidate(); return }
        ValueAnimator.ofFloat(pos, to).apply {
            duration = 240
            interpolator = DecelerateInterpolator()
            addUpdateListener { pos = it.animatedValue as Float; invalidate() }
            start()
        }
    }

    override fun onMeasure(w: Int, h: Int) {
        val d = resources.displayMetrics.density
        setMeasuredDimension((56 * d).toInt(), (32 * d).toInt())
    }

    private fun blend(a: Int, b: Int, t: Float): Int {
        fun ch(s: Int) = ((a shr s and 0xFF) * (1 - t) + (b shr s and 0xFF) * t).toInt() shl s
        return ch(24) or ch(16) or ch(8) or ch(0)
    }

    override fun onDraw(canvas: Canvas) {
        val h = height.toFloat()
        rect.set(0f, 0f, width.toFloat(), h)
        p.color = blend(track, ink, pos)
        canvas.drawRoundRect(rect, h / 2, h / 2, p)
        val pad = h * 0.14f
        val r = h / 2 - pad
        val cx = pad + r + (width - 2 * (pad + r)) * pos
        p.color = blend(ink, canvasColor, pos)
        canvas.drawCircle(cx, h / 2, r, p)
        if (pos > 0.5f) {
            p.color = red
            p.alpha = ((pos - 0.5f) * 2 * 255).toInt()
            canvas.drawCircle(cx, h / 2, r * 0.32f, p)
            p.alpha = 255
        }
    }
}
