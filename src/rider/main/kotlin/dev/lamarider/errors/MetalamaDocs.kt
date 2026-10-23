package dev.lamarider.errors

import com.intellij.ide.BrowserUtil
import com.intellij.openapi.application.ApplicationManager
import java.net.HttpURLConnection
import java.net.URI
import java.net.URLEncoder

/** The documentation of a Metalama diagnostic (LAMA0115): its article when Metalama publishes one, else a web search. */
object MetalamaDocs {
    fun article(id: String) = "https://doc.metalama.net/errors/${id.lowercase()}"

    fun search(id: String) = "https://www.google.com/search?q=" + URLEncoder.encode("Metalama $id", Charsets.UTF_8)

    /** [exists]: whether a URL answers 200 (following redirects). */
    fun url(id: String, exists: (String) -> Boolean): String = article(id).takeIf(exists) ?: search(id)

    fun open(id: String) = ApplicationManager.getApplication().executeOnPooledThread { BrowserUtil.browse(url(id, ::exists)) }

    private fun exists(url: String): Boolean = runCatching {
        var target = URI(url).toURL()
        repeat(5) { // redirects across hosts (doc.metalama.net -> doc.postsharp.net) aren't followed automatically
            val connection = (target.openConnection() as HttpURLConnection).apply {
                requestMethod = "HEAD"; connectTimeout = 3000; readTimeout = 3000; instanceFollowRedirects = false
            }
            val code = connection.responseCode
            if (code in 300..399) target = URI(url).resolve(connection.getHeaderField("Location")).toURL() else return@runCatching code == 200
        }
        false
    }.getOrDefault(false)
}
