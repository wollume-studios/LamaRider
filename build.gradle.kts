import java.time.LocalDateTime
import java.time.format.DateTimeFormatter
import com.jetbrains.plugin.structure.base.utils.isFile
import org.jetbrains.intellij.platform.gradle.Constants
import org.jetbrains.intellij.platform.gradle.TestFrameworkType

plugins {
    id("java")
    alias(libs.plugins.kotlinJvm)
    id("org.jetbrains.intellij.platform") version "2.19.0"     // See https://github.com/JetBrains/intellij-platform-gradle-plugin/releases
    id("me.filippov.gradle.jvm.wrapper") version "0.15.0"
}

val DotnetSolution: String by project
val BuildConfiguration: String by project
val ProductVersion: String by project
val SdkVersion: String by project
val SinceBuild: String by project
val DotnetPluginId: String by project
val RiderPluginId: String by project

allprojects {
    repositories {
        maven { setUrl("https://cache-redirector.jetbrains.com/maven-central") }
    }
}

repositories {
    intellijPlatform {
        defaultRepositories()
        jetbrainsRuntime()
    }
}

// Unit tests run without the IntelliJ Platform on the classpath (its bundled JUnit clashes and nothing here needs it).
val unitTestRuntime: Configuration by configurations.creating

val unitTest by tasks.registering(Test::class) {
    description = "Frontend unit tests (no IDE)"
    group = "verification"
    useJUnitPlatform()
    testClassesDirs = sourceSets.test.get().output.classesDirs
    classpath = sourceSets.test.get().output + sourceSets.main.get().output + unitTestRuntime
}

// UI tests: a real Rider with the built plugin and the sample solution, driven through actions and IDE APIs (no OS
// mouse/keyboard, so they don't need focus). Uses the Rider installation in LAMARIDER_RIDER, else the default Windows install.
val uiTest by tasks.registering(Test::class) {
    description = "UI tests in a real Rider (slow; see src/rider/uiTest)"
    group = "verification"
    val uiTestSources = sourceSets.getByName("uiTest")
    testClassesDirs = uiTestSources.output.classesDirs
    classpath = uiTestSources.runtimeClasspath
    // Starter brings the vintage engine without JUnit 4. Screenshots for docs/screenshots (ScreenshotsUiTest) only with
    // -PuiTestScreenshots, and then only those.
    val screenshots = providers.gradleProperty("uiTestScreenshots").isPresent
    useJUnitPlatform {
        excludeEngines("junit-vintage")
        if (screenshots) includeTags("screenshots") else excludeTags("screenshots")
    }
    val prepareSandbox = tasks.named<org.jetbrains.intellij.platform.gradle.tasks.PrepareSandboxTask>("prepareSandbox")
    dependsOn(prepareSandbox)
    val pluginDir = prepareSandbox.flatMap { it.pluginDirectory }
    val rider = providers.environmentVariable("LAMARIDER_RIDER")
    jvmArgumentProviders += CommandLineArgumentProvider {
        listOfNotNull(
            "-Dlamarider.plugin=${pluginDir.get().asFile.absolutePath}",
            "-Dlamarider.sample=${file("samples/MetalamaSample").absolutePath}",
            // Sample copies with dotnet build/test output: on disk, not in a small /tmp RAM disk.
            "-Djava.io.tmpdir=${layout.buildDirectory.dir("ui-test-tmp").get().asFile.apply { mkdirs() }.absolutePath}",
            rider.orNull?.let { "-Dlamarider.rider=$it" },
            "-Dlamarider.screenshots=${layout.buildDirectory.dir("screenshots").get().asFile.absolutePath}",
            "-Djava.awt.headless=false", // screenshots: java.awt.Robot on the test's display
        )
    }
    testLogging { events("passed", "failed", "skipped"); showStandardStreams = true }
}

tasks.test {
    enabled = false // the IntelliJ test harness is not needed; see unitTest
    dependsOn(unitTest)
}

// publishLocal stamps a build number so Rider sees every local build as an update.
val isLocalPublish = gradle.startParameter.taskNames.any { it.substringAfterLast(':') == "publishLocal" }
// The ReSharper backend parses the version as System.Version: at most 4 parts, each <= Int32.MaxValue.
version = (extra["PluginVersion"] as String).let { base ->
    if (!isLocalPublish) base
    else base.split('.').take(2).joinToString(".") + "." + LocalDateTime.now().format(DateTimeFormatter.ofPattern("yyMMdd.HHmm"))
}.also { v ->
    val parts = v.split('.')
    require(parts.size <= 4 && parts.all { it.toLongOrNull()?.let { n -> n in 0..Int.MAX_VALUE } == true }) {
        "Plugin version '$v' is not a valid System.Version (max 4 numeric parts, each <= ${Int.MAX_VALUE})"
    }
}

tasks.processResources {
    from("dependencies.json") { into("META-INF") }
}

sourceSets {
    main {
        java.srcDir("src/rider/main/java")
        kotlin.srcDir("src/rider/main/kotlin")
        resources.srcDir("src/rider/main/resources")
    }
    test {
        kotlin.srcDir("src/rider/test/kotlin")
    }
    // UI tests: Starter launches a real Rider with the built plugin, Driver talks to it (see src/rider/uiTest).
    create("uiTest") {
        kotlin.srcDir("src/rider/uiTest/kotlin")
    }
}

val uiTestImplementation: Configuration by configurations.getting

val compileDotNet by tasks.registering {
    dependsOn(":protocol:rdgen")
    doLast {
        providers.exec {
            executable("dotnet")
            args("msbuild", DotnetSolution, "/p:Configuration=${BuildConfiguration}", "/p:HostFullIdentifier=", "/p:SdkVersion=$SdkVersion", "/t:Restore;Rebuild", "/v:minimal")
            workingDir(rootDir)
        }.result.get()
    }
}

tasks.compileKotlin {
    dependsOn(":protocol:rdgen")
}

val testDotNet by tasks.registering {
    doLast {
        providers.exec {
            executable("dotnet")
            args("test", DotnetSolution)
            workingDir(rootDir)
        }.result.get()
    }
}

tasks.buildPlugin {
    doLast {
        copy {
            from("${layout.buildDirectory.get()}/distributions/${rootProject.name}-${version}.zip")
            into("${rootDir}/output")
        }
    }
}

// Local plugin repository: Settings › Plugins › ⚙ › Manage Plugin Repositories… › add the printed updatePlugins.xml URL.
// ./gradlew publishLocal [-PlocalRepoDir=<dir>]
val publishLocal by tasks.registering {
    group = "publishing"
    description = "Publish the plugin zip to a local custom plugin repository (updatePlugins.xml)"
    dependsOn(tasks.buildPlugin)
    val repoDir = providers.gradleProperty("localRepoDir")
        .orElse(System.getProperty("user.home") + "/.lamarider/plugin-repo")
    val zip = tasks.buildPlugin.flatMap { it.archiveFile }
    val pluginId = RiderPluginId
    val pluginVersion = version.toString()
    doLast {
        val dir = file(repoDir.get()).apply { mkdirs() }
        val target = File(dir, zip.get().asFile.name)
        zip.get().asFile.copyTo(target, overwrite = true)
        // keep the three newest builds
        dir.listFiles { f -> f.extension == "zip" }!!.sortedByDescending { it.lastModified() }.drop(3).forEach { it.delete() }
        val xml = File(dir, "updatePlugins.xml")
        xml.writeText(
            """
            |<?xml version="1.0" encoding="UTF-8"?>
            |<plugins>
            |  <plugin id="$pluginId" url="${target.toPath().toUri()}" version="$pluginVersion">
            |    <idea-version since-build="$SinceBuild"/>
            |    <name>LamaRider</name>
            |    <vendor>LamaRider</vendor>
            |    <description>Metalama tooling for Rider: Aspect Explorer, Code Vision, T# highlighting, Metalama Diff.</description>
            |  </plugin>
            |</plugins>
            |""".trimMargin()
        )
        println("LamaRider $pluginVersion published. Repository URL: ${xml.toPath().toUri()}")
    }
}

dependencies {
    intellijPlatform {
        rider(ProductVersion) {
            useInstaller = false
        }
        jetbrainsRuntime()
        bundledModule("intellij.platform.debugger") // XDebugger API: breakpoints in compile-time code
    }
    // Plain unit tests for frontend logic that needs no IDE
    testImplementation(libs.kotlinStdLib)
    testImplementation("org.junit.jupiter:junit-jupiter:5.13.4")
    unitTestRuntime(libs.kotlinStdLib)
    unitTestRuntime("org.junit.jupiter:junit-jupiter:5.13.4")
    unitTestRuntime("org.junit.platform:junit-platform-launcher:1.13.4")

    intellijPlatform {
        // Pinned: the UI tests drive any Rider (LAMARIDER_RIDER) with one Starter/Driver, whatever ProductVersion builds the plugin.
        testFramework(TestFrameworkType.Starter, version = "263.6800.24", configurationName = "uiTestImplementation")
    }
    uiTestImplementation("org.junit.jupiter:junit-jupiter:5.13.4")
    uiTestImplementation("org.junit.platform:junit-platform-launcher:1.13.4")
    uiTestImplementation("org.kodein.di:kodein-di-jvm:7.20.2")
    // Starter (pinned to 2026.3) needs runBlockingK from JetBrains' coroutines fork, which Rider 2026.3 ships and 2026.2
    // does not: take it from the Rider under test (LAMARIDER_RIDER, else the default install), or name one
    // (-PuiTestCoroutinesJar=<2026.3 rider>/lib/intellij.libraries.kotlinx.coroutines.core.jar) when testing 2026.2.
    "uiTestCompileOnly"("org.jetbrains.kotlinx:kotlinx-coroutines-core-jvm:1.10.1")
    val coroutinesJar = providers.gradleProperty("uiTestCoroutinesJar").orNull
        ?: listOfNotNull(System.getenv("LAMARIDER_RIDER"), System.getenv("LOCALAPPDATA")?.let { "$it/Programs/Rider" })
            .map { file("$it/lib/intellij.libraries.kotlinx.coroutines.core.jar") }.firstOrNull { it.isFile }?.path
    "uiTestRuntimeOnly"(if (coroutinesJar != null) files(coroutinesJar) else files(configurations.named("intellijPlatformClasspath").map { platform ->
        platform.filter { it.name == "intellij.libraries.kotlinx.coroutines.core.jar" }
    }))
}


tasks.runIde {
    // Match Rider's default heap size of 1.5Gb (default for runIde is 512Mb)
    maxHeapSize = "1500m"
    // ./gradlew runIde -PopenSolution=samples/MetalamaSample/MetalamaSample.sln
    val openSolution = providers.gradleProperty("openSolution")
    argumentProviders += CommandLineArgumentProvider { listOfNotNull(openSolution.orNull?.let { file(it).absolutePath }) }
    // Passing a solution explicitly means trusting it: skip the sandbox's modal "Trust solution" dialog.
    jvmArgumentProviders += CommandLineArgumentProvider { if (openSolution.isPresent) listOf("-Didea.trust.all.projects=true") else emptyList() }
    // ./gradlew runIde -PpluginRepo=<updatePlugins.xml URL> : test update checks against a custom repository
    val pluginRepo = providers.gradleProperty("pluginRepo")
    jvmArgumentProviders += CommandLineArgumentProvider { listOfNotNull(pluginRepo.orNull?.let { "-Didea.plugin.hosts=$it" }) }
}

tasks.patchPluginXml {
    sinceBuild.set(SinceBuild)
    untilBuild.set(provider { null })

    // docs/plugin-description.html; images need public URLs (Marketplace, Settings › Plugins), e.g.
    // ScreenshotBaseUrl=https://raw.githubusercontent.com/wollume-studios/LamaRider/main/docs/screenshots
    val screenshotBaseUrl = providers.gradleProperty("ScreenshotBaseUrl").orElse("")
    val descriptionFile = file("${rootDir}/docs/plugin-description.html")
    inputs.file(descriptionFile)
    pluginDescription.set(screenshotBaseUrl.map { base ->
        val html = descriptionFile.readText().replace(Regex("""(?s)<!--.*?-->\s*"""), "")
        if (base.isBlank()) html.replace(Regex("""<p><img [^>]*></p>\s*"""), "")
        else html.replace("{{screenshots}}", base.trimEnd('/'))
    })

    val changelogText = file("${rootDir}/CHANGELOG.md").readText()
    val changelogMatches = Regex("(?s)(-.+?)(?=##|\$)").findAll(changelogText)
    changeNotes.set(changelogMatches.map {
        it.groups[1]!!.value.replace("(?s)\r?\n".toRegex(), "<br />\n")
    }.take(1).joinToString())
}

// All sandbox tasks (buildPlugin uses prepareSandbox, runIde uses prepareSandbox_runIde).
tasks.withType<org.jetbrains.intellij.platform.gradle.tasks.PrepareSandboxTask>().configureEach {
    dependsOn(compileDotNet)

    val outputFolder = "${rootDir}/src/dotnet/${DotnetPluginId}/bin/${DotnetPluginId}.Rider/${BuildConfiguration}"
    val dllFiles = listOf(
        "$outputFolder/${DotnetPluginId}.dll",
        "$outputFolder/${DotnetPluginId}.pdb",
    )

    dllFiles.forEach { f ->
        from(file(f)) { into("${rootProject.name}/dotnet") }
    }

    // Worker source: the backend builds it on demand against the solution's Metalama version.
    from("src/worker/LamaRider.Worker") {
        into("${rootProject.name}/worker")
        exclude("bin/**", "obj/**")
    }

    doLast {
        dllFiles.forEach { f ->
            val file = file(f)
            if (!file.exists()) throw RuntimeException("File $file does not exist")
        }
    }
}

// JetBrains Marketplace: PUBLISH_TOKEN=<token> ./gradlew publishPlugin [-PPublishChannel=eap]
// The first upload of a new plugin must be done manually on plugins.jetbrains.com.
intellijPlatform {
    publishing {
        token = providers.environmentVariable("PUBLISH_TOKEN")
        channels = providers.gradleProperty("PublishChannel").map { listOf(it) }.orElse(listOf("eap"))
    }
}

val riderModel: Configuration by configurations.creating {
    isCanBeConsumed = true
    isCanBeResolved = false
}

artifacts {
    add(riderModel.name, provider {
        intellijPlatform.platformPath.resolve("lib/rd/rider-model.jar").also {
            check(it.isFile) {
                "rider-model.jar is not found at $riderModel"
            }
        }
    }) {
        builtBy(Constants.Tasks.INITIALIZE_INTELLIJ_PLATFORM_PLUGIN)
    }
}
