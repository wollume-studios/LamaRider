import com.jetbrains.plugin.structure.base.utils.isFile
import org.jetbrains.intellij.platform.gradle.Constants

plugins {
    id("java")
    alias(libs.plugins.kotlinJvm)
    id("org.jetbrains.intellij.platform") version "2.19.0"     // See https://github.com/JetBrains/intellij-platform-gradle-plugin/releases
    id("me.filippov.gradle.jvm.wrapper") version "0.15.0"
}

val DotnetSolution: String by project
val BuildConfiguration: String by project
val ProductVersion: String by project
val DotnetPluginId: String by project

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

tasks.test {
    enabled = false // the IntelliJ test harness is not needed; see unitTest
    dependsOn(unitTest)
}

version = extra["PluginVersion"] as String

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
}

val compileDotNet by tasks.registering {
    dependsOn(":protocol:rdgen")
    doLast {
        providers.exec {
            executable("dotnet")
            args("msbuild", DotnetSolution, "/p:Configuration=${BuildConfiguration}", "/p:HostFullIdentifier=", "/t:Restore;Rebuild", "/v:minimal")
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

dependencies {
    intellijPlatform {
        rider(ProductVersion) {
            useInstaller = false
        }
        jetbrainsRuntime()
    }
    // Plain unit tests for frontend logic that needs no IDE
    testImplementation(libs.kotlinStdLib)
    testImplementation("org.junit.jupiter:junit-jupiter:5.13.4")
    unitTestRuntime(libs.kotlinStdLib)
    unitTestRuntime("org.junit.jupiter:junit-jupiter:5.13.4")
    unitTestRuntime("org.junit.platform:junit-platform-launcher:1.13.4")
}

tasks.runIde {
    // Match Rider's default heap size of 1.5Gb (default for runIde is 512Mb)
    maxHeapSize = "1500m"
    // ./gradlew runIde -PopenSolution=samples/MetalamaSample/MetalamaSample.sln
    val openSolution = providers.gradleProperty("openSolution")
    argumentProviders += CommandLineArgumentProvider { listOfNotNull(openSolution.orNull?.let { file(it).absolutePath }) }
    // Passing a solution explicitly means trusting it: skip the sandbox's modal "Trust solution" dialog.
    jvmArgumentProviders += CommandLineArgumentProvider { if (openSolution.isPresent) listOf("-Didea.trust.all.projects=true") else emptyList() }
}

tasks.patchPluginXml {
    sinceBuild.set("263")
    untilBuild.set(provider { null })

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
