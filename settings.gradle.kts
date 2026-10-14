pluginManagement {
    // Provide repositories to resolve plugins
    repositories {
        maven { setUrl("https://cache-redirector.jetbrains.com/plugins.gradle.org") }
        maven { setUrl("https://cache-redirector.jetbrains.com/maven-central") }
        maven { setUrl("https://cache-redirector.jetbrains.com/dl.bintray.com/kotlin/kotlin-eap") }
    }
    resolutionStrategy {
        eachPlugin {
            // Gradle has to map a plugin dependency to Maven coordinates - '{groupId}:{artifactId}:{version}'. It tries
            // to do use '{plugin.id}:{plugin.id}.gradle.plugin:version'.
            // This doesn't work for rdgen, so we provide some help
            if (requested.id.id == "com.jetbrains.rdgen") {
                useModule("com.jetbrains.rd:rd-gen:${requested.version}")
            }
        }
    }
}

plugins {
    // Auto-provisions the JDK required by the targeted IntelliJ Platform (JDK 25 for 263.x).
    id("org.gradle.toolchains.foojay-resolver-convention") version "1.0.0"
}

rootProject.name = "LamaRider"

include(":protocol")
