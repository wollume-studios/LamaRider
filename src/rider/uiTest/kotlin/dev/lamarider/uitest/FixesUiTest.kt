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
 * applied and the code compared with the sample's fixed version. The report lands in build/fix-selftest.json.
 */
class FixesUiTest : UiTestBase() {
    @Test
    fun `quick-fixes turn Metalama's error samples into their fixed versions`() = withRider("fixes", sample = "MetalamaFixSamples") {
        val report = solutionDir.resolve("fix-selftest.json")
        // The worker analyzes ~200 small projects first: run until the report has the samples' diagnostics.
        waitFor("self-test report with diagnostics", timeout = 30.minutes, interval = 30.seconds) {
            Files.deleteIfExists(report)
            invokeAction("LamaRider.SelfTestFixes")
            waitFor("report written", timeout = 10.minutes, interval = 5.seconds) { Files.exists(report) }
            Regex(""""Reported": \[\s*"LAMA""").containsMatchIn(Files.readString(report))
        }
        val copy = Path.of(System.getProperty("lamarider.plugin")).parent.parent.resolve("fix-selftest.json")
        Files.copy(report, copy, StandardCopyOption.REPLACE_EXISTING)
        println("Fix self-test report: $copy")
    }
}
