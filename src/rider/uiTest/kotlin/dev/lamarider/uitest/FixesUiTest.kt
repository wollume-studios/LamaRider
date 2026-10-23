package dev.lamarider.uitest

import com.intellij.driver.sdk.invokeAction
import com.intellij.driver.sdk.waitFor
import org.junit.jupiter.api.Test
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import kotlin.time.Duration.Companion.minutes
import kotlin.time.Duration.Companion.seconds

/**
 * Every quick-fix for Metalama diagnostics, on Metalama's own error samples (samples/MetalamaFixSamples): each fix is
 * applied and the code compared with the sample's fixed version. The samples are opened in batches (Batch01.sln ...):
 * Rider watches every project folder and Linux allows 128 inotify instances per user. Reports: build/fix-selftest-NN.json.
 */
class FixesUiTest : UiTestBase() {
    @Test
    fun `quick-fixes turn Metalama's error samples into their fixed versions`() {
        val batches = Path.of(System.getProperty("lamarider.sample")).resolveSibling("MetalamaFixSamples")
            .toFile().list { _, name -> name.startsWith("Batch") && name.endsWith(".sln") }!!.map { it.removeSuffix(".sln") }.sorted()
        val reports = Path.of(System.getProperty("lamarider.plugin")).parent.parent
        for (batch in batches) {
            withRider("fixes-$batch", sample = "MetalamaFixSamples", solution = batch) {
                val report = solutionDir.resolve("fix-selftest.json")
                val progress = solutionDir.resolve("fix-selftest.json.progress")
                // The signal is dropped while LamaRider's backend connection isn't up yet: ask again until the handler has started.
                waitFor("self-test handler started ($batch)", timeout = 15.minutes, interval = 20.seconds) {
                    if (!Files.exists(progress)) invokeAction("LamaRider.SelfTestFixes")
                    Files.exists(progress)
                }
                runCatching {
                    waitFor("report written ($batch)", timeout = 30.minutes, interval = 10.seconds) { Files.exists(report) }
                }.onFailure {
                    println("self-test progress ($batch): " + Files.readAllLines(progress).takeLast(5))
                    throw it
                }
                // The first pass can run before the worker has analyzed the batch (no diagnostics yet): once more.
                if (!Regex(""""Reported": \[\s*"LAMA""").containsMatchIn(Files.readString(report))) {
                    Files.delete(report)
                    Files.delete(progress)
                    waitFor("self-test report with diagnostics ($batch)", timeout = 25.minutes, interval = 30.seconds) {
                        if (!Files.exists(progress)) invokeAction("LamaRider.SelfTestFixes")
                        Files.exists(report) && Regex(""""Reported": \[\s*"LAMA""").containsMatchIn(Files.readString(report)).also {
                            if (!it) { Files.deleteIfExists(report); Files.deleteIfExists(progress) }
                        }
                    }
                }
                Files.copy(report, reports.resolve("fix-selftest-$batch.json"), StandardCopyOption.REPLACE_EXISTING)
            }
        }
    }
}
