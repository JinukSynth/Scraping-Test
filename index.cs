using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.UIA3;

namespace VegasMonitorPoC
{
    class Program
    {
        // 전역 후킹을 위한 Win32 API 임포트
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr hInstance, uint threadId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hookId);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hookId, int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        // 마우스 이벤트 콜백 델리게이트
        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        // 마우스 후킹 ID
        private static IntPtr _mouseHookID = IntPtr.Zero;
        private static LowLevelMouseProc _mouseProc;

        static void Main(string[] args)
        {
            try
            {
                Console.WriteLine("Vegas.exe 모니터링 PoC 시작...");
                
                // 1. Vegas.exe 프로세스에 연결
                Process[] processes = Process.GetProcessesByName("Vegas");
                if (processes.Length == 0)
                {
                    Console.WriteLine("Vegas.exe 프로세스를 찾을 수 없습니다. 프로그램이 실행 중인지 확인하세요.");
                    Console.WriteLine("다른 이름으로 실행 중일 수 있습니다. 실행 중인 프로세스 목록:");
                    foreach (var proc in Process.GetProcesses())
                    {
                        if (proc.MainWindowTitle.Length > 0)
                        {
                            Console.WriteLine($"- {proc.ProcessName} (ID: {proc.Id}, 창 제목: {proc.MainWindowTitle})");
                        }
                    }
                    Console.Write("연결할 프로세스 이름을 입력하세요: ");
                    string processName = Console.ReadLine().Trim();
                    if (string.IsNullOrEmpty(processName))
                    {
                        Console.WriteLine("프로세스 이름이 입력되지 않았습니다. 종료합니다.");
                        return;
                    }
                    processes = Process.GetProcessesByName(processName);
                    if (processes.Length == 0)
                    {
                        Console.WriteLine($"{processName} 프로세스를 찾을 수 없습니다. 종료합니다.");
                        return;
                    }
                }

                var app = Application.Attach(processes[0].Id);
                Console.WriteLine($"프로세스 {processes[0].ProcessName} (ID: {processes[0].Id})에 연결됨");

                using (var automation = new UIA3Automation())ㅌㅈ
                {
                    // 2. 메인 창 가져오기
                    var mainWindow = app.GetMainWindow(automation);
                    Console.WriteLine($"메인 창 제목: {mainWindow.Title}");

                    // 3. UI 요소 검색 및 카운트
                    var allButtons = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                    Console.WriteLine($"발견된 버튼 수: {allButtons.Length}");

                    var allTextBoxes = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                    Console.WriteLine($"발견된 텍스트 필드 수: {allTextBoxes.Length}");

                    // 몇 개의 버튼 정보 출력
                    Console.WriteLine("\n--- 버튼 샘플 정보 ---");
                    foreach (var button in allButtons.Take(5))
                    {
                        Console.WriteLine($"버튼: 이름={button.Name}, ID={button.AutomationId}, 활성화={button.IsEnabled}");
                    }

                    // 4. CEF 관련 컨트롤 찾기
                    Console.WriteLine("\n--- CEF 브라우저 요소 검색 ---");
                    var cefElements = mainWindow.FindAllDescendants(cf =>
                        cf.ByName("CefSharpBrowser") ||
                        cf.ByClassName("CefSharpBrowser") ||
                        cf.ByAutomationId("chromiumWebBrowser") ||
                        cf.ByClassName("Chrome_WidgetWin_1") ||  // Chromium 윈도우 클래스
                        cf.ByClassName("CefBrowserWindow")       // 다른 가능한 클래스 이름
                    );

                    Console.WriteLine($"발견된 CEF 요소 수: {cefElements.Length}");

                    if (cefElements.Length > 0)
                    {
                        Console.WriteLine("CEF 요소 정보:");
                        foreach (var element in cefElements)
                        {
                            Console.WriteLine($"- 유형: {element.ControlType}, 이름: {element.Name}, 클래스: {element.ClassName}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("CEF 요소를 찾지 못했습니다. DOM 접근이 불가능할 수 있습니다.");
                        
                        // 추가 시도: 모든 패널/컨테이너 검색
                        Console.WriteLine("\n대안: 모든 패널/컨테이너 검색 중...");
                        var containers = mainWindow.FindAllDescendants(cf => 
                            cf.ByControlType(ControlType.Pane) || 
                            cf.ByControlType(ControlType.Custom)
                        );
                        
                        foreach (var container in containers.Take(10))
                        {
                            Console.WriteLine($"컨테이너: 이름={container.Name}, 클래스={container.ClassName}");
                            if (container.ClassName != null && 
                                (container.ClassName.Contains("Chrome") || 
                                 container.ClassName.Contains("Cef") || 
                                 container.ClassName.Contains("Web")))
                            {
                                Console.WriteLine("  ^ 이 요소가 CEF 브라우저일 가능성 있음!");
                            }
                        }
                    }

                    // 5. 텍스트 필드 변경 이벤트 구독
                    if (allTextBoxes.Length > 0)
                    {
                        Console.WriteLine("\n--- 텍스트 필드 변경 모니터링 ---");
                        var textBox = allTextBoxes[0];
                        Console.WriteLine($"모니터링 중인 텍스트 필드: {textBox.Name}");
                        
                        automation.RegisterPropertyChangedEvent(textBox.AutomationElement, (sender, e) => {
                            if (e.Property == PropertyId.ValueValue)
                            {
                                Console.WriteLine($"텍스트 변경됨: {textBox.AsTextBox().Text}");
                            }
                        });
                        
                        Console.WriteLine("텍스트 필드에 입력해보세요. 변경 내용이 여기에 표시됩니다.");
                    }

                    // 6. 전역 마우스 후킹 시작
                    Console.WriteLine("\n--- 전역 마우스 후킹 시작 ---");
                    _mouseProc = MouseHookCallback;
                    _mouseHookID = SetHook(_mouseProc);
                    Console.WriteLine("마우스 클릭 시 이벤트가 여기에 표시됩니다.");
                    
                    Console.WriteLine("\n종료하려면 아무 키나 누르세요...");
                    Console.ReadKey();
                    
                    // 후킹 해제
                    if (_mouseHookID != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_mouseHookID);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"오류 발생: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }
            
            Console.WriteLine("프로그램을 종료합니다. 아무 키나 누르세요...");
            Console.ReadKey();
        }

        // 마우스 후킹 설정
        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                return SetWindowsHookEx(14, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        // 마우스 이벤트 콜백
        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                switch ((int)wParam)
                {
                    case 0x0201: // WM_LBUTTONDOWN
                        Console.WriteLine("왼쪽 마우스 버튼 클릭 감지!");
                        break;
                    case 0x0204: // WM_RBUTTONDOWN
                        Console.WriteLine("오른쪽 마우스 버튼 클릭 감지!");
                        break;
                }
            }
            return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
        }
    }
}