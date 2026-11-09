import java.util.Properties

plugins {
    // Kotlin is built into AGP 9: no separate kotlin-android plugin here.
    id("com.android.application")
}

android {
    namespace = "com.padlink"
    compileSdk = 35

    defaultConfig {
        applicationId = "com.padlink"
        minSdk = 26
        targetSdk = 35
        versionCode = 2
        versionName = "0.2.0"
    }

    // Release key lives outside the repo; keystore.properties (gitignored) points at it.
    // Without it, release builds fall back to the debug key.
    val propsFile = rootProject.file("keystore.properties")
    if (propsFile.exists()) {
        val props = Properties()
        propsFile.inputStream().use<java.io.InputStream, Unit> { props.load(it) }
        signingConfigs.create("release") {
            storeFile = file(props["storeFile"] as String)
            storePassword = props["storePassword"] as String
            keyAlias = props["keyAlias"] as String
            keyPassword = props["keyPassword"] as String
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = false
            signingConfig = signingConfigs.findByName("release") ?: signingConfigs.getByName("debug")
        }
    }

    // Bytecode level for Android; independent of the JDK (26) that runs Gradle.
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

// No dependencies: plain Android framework only.
