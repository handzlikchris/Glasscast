// The phone companion for phone mode (architecture/phone-mode.md). No AndroidX: the platform APIs
// and two libraries (prebuilt libwebrtc, OkHttp for the WebSocket) are all it needs.
import java.util.Properties

plugins {
    id("com.android.application")
}

// The companion socket the setup screen starts with, e.g. wss://glasses.example.com/ws/companion:
// glassesServer=... in android-companion/local.properties (git-ignored) or as a Gradle property.
// Empty means the user types the address in.
val localProperties = Properties().also { props ->
    rootProject.file("local.properties").takeIf { it.exists() }?.inputStream()?.use { props.load(it) }
}
val glassesServer: String =
    localProperties.getProperty("glassesServer") ?: providers.gradleProperty("glassesServer").orNull ?: ""

android {
    namespace = "uk.co.reliablesolutions.glassesremote.companion"
    compileSdk = 35

    defaultConfig {
        applicationId = "uk.co.reliablesolutions.glassesremote.companion"
        // Android 11: ACTION_IME_ENTER, WindowMetrics. The S25 runs 15/16.
        minSdk = 30
        targetSdk = 35
        versionCode = 1
        versionName = "0.1"
        buildConfigField("String", "DEFAULT_SERVER", "\"$glassesServer\"")
    }

    buildFeatures {
        buildConfig = true
    }

    buildTypes {
        release {
            isMinifyEnabled = false
        }
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

dependencies {
    implementation("io.getstream:stream-webrtc-android:1.3.10")
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    // The square screen for the glasses calls the window manager's hidden forced size/density.
    implementation("org.lsposed.hiddenapibypass:hiddenapibypass:6.1")

    testImplementation("junit:junit:4.13.2")
    // Android's org.json is a stub in local unit tests; the real one for the parser tests.
    testImplementation("org.json:json:20240303")
}
