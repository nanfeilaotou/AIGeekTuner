using AIGeekTuner.Configuration;

namespace AIGeekTuner.Tests.Configuration;

public class VoiceSettingsTests
{
    [Fact]
    public void FreshInstallDefaults_UseNeutralVoiceConfiguration()
    {
        var voice = new ApplicationSettings().Voice;

        Assert.False(voice.Enabled);
        Assert.Equal("http://127.0.0.1:9880", voice.Endpoint);
        Assert.Equal(string.Empty, voice.ReferenceAudioPath);
        Assert.Equal(string.Empty, voice.PromptText);
        Assert.Equal("zh", voice.PromptLang);
        Assert.Equal(1.0, voice.SpeedFactor);
        Assert.Equal(string.Empty, voice.GptModelPath);
        Assert.Equal(string.Empty, voice.SovitsModelPath);
    }

    [Fact]
    public void DisabledVoice_AllowsBlankReferenceAudioPath()
    {
        var settings = new ApplicationSettings
        {
            Voice = new VoiceSettings
            {
                Enabled = false,
                ReferenceAudioPath = string.Empty,
            },
        };

        Assert.Empty(ApplicationSettingsValidator.Validate(settings));
    }

    [Fact]
    public void EnabledVoice_RequiresReferenceAudioPath()
    {
        var settings = new ApplicationSettings
        {
            Voice = new VoiceSettings
            {
                Enabled = true,
                ReferenceAudioPath = string.Empty,
            },
        };

        var errors = ApplicationSettingsValidator.Validate(settings);

        Assert.Contains(errors, error => error.Contains("参考音频路径"));
    }
}
