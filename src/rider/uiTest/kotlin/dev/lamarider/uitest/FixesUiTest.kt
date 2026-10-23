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
                // The worker analyzes the batch first: repeat until the report has the samples' own diagnostics.
                waitFor("self-test report with diagnostics ($batch)", timeout = 25.minutes, interval = 20.seconds) {
                    Files.deleteIfExists(report)
                    invokeAction("LamaRider.SelfTestFixes")
                    val progress = solutionDir.resolve("fix-selftest.json.progress")
                    runCatching {
                        waitFor("report written", timeout = 8.minutes, interval = 5.seconds) { Files.exists(report) }
                    }.onFailure {
                        println("self-test progress ($batch): " + (if (Files.exists(progress)) Files.readAllLines(progress).takeLast(5) else "handler never started"))
                        throw it
                    }
                    Regex(""""Reported": \[\s*"LAMA""").containsMatchIn(Files.readString(report))
                }
                Files.copy(report, reports.resolve("fix-selftest-$batch.json"), StandardCopyOption.REPLACE_EXISTING)
            }
        }
    }
}
