using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Glide.App;

internal static class CommandContent
{
    public static string Caption(string description) => description.Split('·')[0].Trim() switch
    {
        "화면 설정" => "설정",
        "녹화 시작" => "녹화",
        "발표 시작" => "시작",
        "발표 화면 열기" => "발표 준비",
        "카운트다운 취소" => "취소",
        "녹화 일시정지" => "일시정지",
        "녹화 재개" => "재개",
        "녹화 종료" => "정지",
        "발표 종료" => "종료",
        "위치 고정" => "고정",
        "추적 재개" => "추적",
        "화면 가리기" => "가리기",
        "발표 재개" => "재개",
        "작업 화면" => "홈",
        "저장 폴더" => "폴더",
        "소리 테스트 종료" => "테스트 종료",
        "저장 취소" => "취소",
        "MP4 저장" => "저장",
        "원본 비교" => "원본",
        "편집 결과 보기" => "편집본",
        "소리 변경 적용" => "적용",
        "구간 삭제" => "삭제",
        "이 구간만" => "유지",
        "현재 위치에 확대 추가" => "확대 추가",
        "확대 변경 적용" => "적용",
        "확대 효과 삭제" => "삭제",
        "자동 확대 다시 분석" => "자동 확대",
        "모든 확대 해제" => "확대 해제",
        "실행 취소" => "되돌림",
        "다시 실행" => "재실행",
        "스타일 적용" => "적용",
        "파일 열기" => "열기",
        var name => name
    };

    public static void Set(Button button, string glyph, string caption, double fontSize = 13)
    {
        // Keep the content stable while playback/status updates refresh labels.
        if (button.Content is StackPanel panel && panel.Children.Count == 2 &&
            panel.Children[0] is FontIcon icon && panel.Children[1] is TextBlock text)
        {
            if (icon.Glyph != glyph) icon.Glyph = glyph;
            if (text.Text != caption) text.Text = caption;
            return;
        }
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        content.Children.Add(new FontIcon { Glyph = glyph, FontSize = 18 });
        content.Children.Add(new TextBlock { Text = caption, FontSize = fontSize, VerticalAlignment = VerticalAlignment.Center });
        button.Content = content;
    }
}
