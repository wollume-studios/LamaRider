import com.jetbrains.rd.generator.gradle.RdGenTask

plugins {
    id("org.jetbrains.kotlin.jvm")
    id("com.jetbrains.rdgen") version libs.versions.rdGen
}

dependencies {
    implementation(libs.kotlinStdLib)
    implementation(libs.rdGen)
    implementation(
        project(
            mapOf(
                "path" to ":",
                "configuration" to "riderModel"
            )
        )
    )
}

val DotnetPluginId: String by rootProject
val RiderPluginId: String by rootProject

rdgen {
    val csOutput = File(rootDir, "src/dotnet/${DotnetPluginId}/Rd")
    val ktOutput = File(rootDir, "src/rider/main/kotlin/dev/lamarider/rd")

    verbose = true
    packages = "model.rider"

    generator {
        language = "kotlin"
        transform = "asis"
        root = "com.jetbrains.rider.model.nova.ide.IdeRoot"
        namespace = "com.jetbrains.rider.model"
        directory = "$ktOutput"
    }

    generator {
        language = "csharp"
        transform = "reversed"
        root = "com.jetbrains.rider.model.nova.ide.IdeRoot"
        namespace = "JetBrains.Rider.Model"
        directory = "$csOutput"
    }
}

tasks.withType<RdGenTask> {
    val classPath = sourceSets["main"].runtimeClasspath
    dependsOn(classPath)
    classpath(classPath)
    // rider-model.jar of 263.x is Java 25 bytecode
    javaLauncher.set(javaToolchains.launcherFor { languageVersion.set(JavaLanguageVersion.of(25)) })
}
