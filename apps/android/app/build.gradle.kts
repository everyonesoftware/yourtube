plugins {
    id("com.android.application") version "8.13.2"
}

android {
    namespace = "com.everyonesoftware.yourtube"
    compileSdk = 36

    defaultConfig {
        applicationId = "com.everyonesoftware.yourtube"
        minSdk = 26
        targetSdk = 36
        val ciVersion = providers.gradleProperty("yourtubeBuildVersion").orNull
        if (ciVersion != null) {
            require(Regex("0\\.1\\.[1-9][0-9]{0,8}").matches(ciVersion)) { "Invalid YourTube Android build version" }
        }
        versionCode = ciVersion?.substringAfterLast('.')?.toInt() ?: 1
        versionName = ciVersion ?: "0.1.0-dev"
        testInstrumentationRunner = "androidx.test.runner.AndroidJUnitRunner"
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
}

abstract class PreparePlaybackFixture : DefaultTask() {
    @get:InputDirectory abstract val fixtures: DirectoryProperty
    @get:OutputDirectory abstract val assets: DirectoryProperty
    @get:Inject abstract val files: FileSystemOperations

    @TaskAction fun prepare() {
        files.sync {
            from(fixtures) {
                include("manifest.mpd", "video.mp4", "audio.mp4")
                filesMatching("manifest.mpd") {
                    filter { line: String -> line.replace("https://fixture.invalid/", "") }
                }
            }
            into(assets)
        }
    }
}
androidComponents.onVariants { variant ->
    val testAssets = variant.androidTest?.sources?.assets ?: return@onVariants
    val prepare = tasks.register<PreparePlaybackFixture>("prepare${variant.name.replaceFirstChar(Char::uppercaseChar)}PlaybackFixture") {
        fixtures.set(rootProject.layout.projectDirectory.dir("../windows/YourTube.Playback.NativeValidation/Fixtures"))
    }
    testAssets.addGeneratedSourceDirectory(prepare, PreparePlaybackFixture::assets)
}

dependencies {
    implementation("androidx.activity:activity:1.10.1")
    implementation("androidx.media3:media3-exoplayer:1.8.0")
    implementation("androidx.media3:media3-exoplayer-dash:1.8.0")
    implementation("androidx.media3:media3-ui:1.8.0")
    androidTestImplementation("androidx.test.ext:junit:1.3.0")
    androidTestImplementation("androidx.test:runner:1.7.0")
}
