using System;
using System.Diagnostics;
using System.Linq;              // Take() 사용을 위해 추가
using System.Runtime.InteropServices;
using System.Threading;        // (현재 예제에선 크게 사용 안 됨)
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Identifiers;  // PropertyId를 위해
using FlaUI.UIA3;

namespace VegasMonitorPoC
{
    class Program
    {
        // --------------------------------------------------------------------
        // [1] Win32 API 선언부 (전역 후킹 등에 사용)
        // --------------------------------------------------------------------

        // user32.dll : Windows GUI/입력 처리 관련 API들이 있음

        /// <summary>
        /// SetWindowsHookEx : 특정 Hook(마우스, 키보드 등)을 설치하는 함수
        /// </summary>
        /// <param name="idHook">훅의 종류 (마우스, 키보드 등)</param>
        /// <param name="callback">콜백 델리게이트</param>
        /// <param name="hInstance">모듈 핸들</param>
        /// <param name="threadId">스레드 ID (0이면 모든 스레드에 대해 전역 훅)</param>
        /// <returns>후크 핸들 (IntPtr.Zero면 실패)</returns>
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr hInstance, uint threadId);

        /// <summary>
        /// UnhookWindowsHookEx : 설치된 훅을 해제
        /// </summary>
        /// <param name="hookId">해제할 훅 핸들</param>
        /// <returns>성공/실패 여부</returns>
        [DllImport("user32.dll", SetLastError = true)]
        static extern bool UnhookWindowsHookEx(IntPtr hookId);

        /// <summary>
        /// CallNextHookEx : 훅 체인에서 다음 훅으로 메시지를 전달
        /// </summary>
        /// <param name="hookId">훅 핸들</param>
        /// <param name="nCode">훅 프로시저 코드</param>
        /// <param name="wParam">메시지 (마우스 이벤트 등)</param>
        /// <param name="lParam">추가 정보 (포인터)</param>
        /// <returns>다음 훅에서 반환되는 값</returns>
        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr CallNextHookEx(IntPtr hookId, int nCode, IntPtr wParam, IntPtr lParam);

        // kernel32.dll : Windows 커널 관련 API
        /// <summary>
        /// GetModuleHandle : 현재 프로세스 또는 지정된 모듈의 핸들을 가져옴
        /// </summary>
        /// <param name="lpModuleName">모듈 이름(예: 현재 실행파일명). null이면 실행중인 프로세스 모듈</param>
        /// <returns>모듈 핸들</returns>
        [DllImport("kernel32.dll", SetLastError = true)]
        static extern IntPtr GetModuleHandle(string lpModuleName);

        // 마우스 이벤트를 받기 위한 델리게이트 형식 선언
        // LowLevelMouseProc : WH_MOUSE_LL(저수준 마우스 훅)에서 사용
        delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

        // 전역 마우스 훅을 저장할 핸들과 콜백을 static으로 유지
        private static IntPtr _mouseHookID = IntPtr.Zero;
        private static LowLevelMouseProc _mouseProc;

        static void Main(string[] args)
        {
            try
            {
                Console.WriteLine("Vegas.exe 모니터링 PoC 시작...");

                // --------------------------------------------------------------
                // [2] Vegas.exe 프로세스 찾기
                // --------------------------------------------------------------
                Process[] processes = Process.GetProcessesByName("Vegas");
                if (processes.Length == 0)
                {
                    Console.WriteLine("Vegas.exe 프로세스를 찾을 수 없습니다. 프로그램이 실행 중인지 확인하세요.");
                    Console.WriteLine("다른 이름으로 실행 중일 수 있습니다. 실행 중인 프로세스 목록 (창이 있는 것만):");

                    // 현재 실행 중인 프로세스 중, 창(제목)이 존재하는 것만 출력
                    foreach (var proc in Process.GetProcesses())
                    {
                        if (proc.MainWindowTitle.Length > 0)
                        {
                            Console.WriteLine($"- {proc.ProcessName} (ID: {proc.Id}, 창 제목: {proc.MainWindowTitle})");
                        }
                    }
                    // 사용자에게 프로세스 이름 입력받아 재시도
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

                // Vegas.exe (혹은 입력받은 프로세스) 중 첫 번째 프로세스에 Attach
                var app = Application.Attach(processes[0].Id);
                Console.WriteLine($"프로세스 {processes[0].ProcessName} (ID: {processes[0].Id})에 연결됨");

                // --------------------------------------------------------------
                // [3] FlaUI를 통한 UIAutomation 세팅
                // --------------------------------------------------------------
                using (var automation = new UIA3Automation())
                {
                    // 메인 윈도우 가져오기
                    var mainWindow = app.GetMainWindow(automation);
                    Console.WriteLine($"메인 창 제목: {mainWindow.Title}");

                    // ----------------------------------------------------------
                    // [3-1] 버튼, 텍스트박스 등 UI 요소 검색
                    // ----------------------------------------------------------
                    var allButtons = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
                    Console.WriteLine($"발견된 버튼 수: {allButtons.Length}");

                    var allTextBoxes = mainWindow.FindAllDescendants(cf => cf.ByControlType(ControlType.Edit));
                    Console.WriteLine($"발견된 텍스트 필드 수: {allTextBoxes.Length}");

                    // 버튼 정보 몇 개 출력
                    Console.WriteLine("\n--- 버튼 샘플 정보 (최대 5개) ---");
                    foreach (var button in allButtons.Take(5))
                    {
                        Console.WriteLine($"버튼: 이름={button.Name}, ID={button.AutomationId}, 활성화={button.IsEnabled}");
                    }

                    // ----------------------------------------------------------
                    // [3-2] CEF (Chromium) 요소 탐색
                    // ----------------------------------------------------------
                    Console.WriteLine("\n--- CEF 브라우저 요소 검색 ---");
                    var cefElements = mainWindow.FindAllDescendants(cf =>
                        cf.ByName("CefSharpBrowser") ||
                        cf.ByClassName("CefSharpBrowser") ||
                        cf.ByAutomationId("chromiumWebBrowser") ||
                        cf.ByClassName("Chrome_WidgetWin_1") ||  // 크로미움 공용 클래스
                        cf.ByClassName("CefBrowserWindow")       // 다른 CEF 클래스명
                    );

                    Console.WriteLine($"발견된 CEF 요소 수: {cefElements.Length}");

                    if (cefElements.Length > 0)
                    {
                        // 만약 하나라도 찾았다면, 그것들 출력
                        Console.WriteLine("CEF 요소 정보:");
                        foreach (var element in cefElements)
                        {
                            Console.WriteLine($"- 유형: {element.ControlType}, 이름: {element.Name}, 클래스: {element.ClassName}");
                        }
                    }
                    else
                    {
                        Console.WriteLine("CEF 요소를 찾지 못했습니다. DOM 접근이 불가능할 수 있습니다.");
                        
                        // 추가로 Pane/Custom 컨트롤을 검색해 CEF 흔적 찾는 예
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

                    // ----------------------------------------------------------
                    // [3-3] 텍스트박스 변경 이벤트 구독
                    // ----------------------------------------------------------
                    if (allTextBoxes.Length > 0)
                    {
                        Console.WriteLine("\n--- 텍스트 필드 변경 모니터링 ---");
                        var textBox = allTextBoxes[0];
                        Console.WriteLine($"모니터링 중인 텍스트 필드: {textBox.Name}");

                        // PropertyChanged 이벤트 등록 (ValueValue 변화 감지)
                        automation.RegisterPropertyChangedEvent(
                            textBox.AutomationElement,
                            (sender, e) =>
                            {
                                if (e.Property == PropertyId.ValueValue)
                                {
                                    // 텍스트 내용 갱신시 콘솔에 출력
                                    Console.WriteLine($"텍스트 변경됨: {textBox.AsTextBox().Text}");
                                }
                            }
                        );

                        Console.WriteLine("텍스트 필드에 입력해보세요. 변경 내용이 여기에 표시됩니다.");
                    }

                    // ----------------------------------------------------------
                    // [4] 전역 마우스 후킹 (왼/오른쪽 클릭 감지)
                    // ----------------------------------------------------------
                    Console.WriteLine("\n--- 전역 마우스 후킹 시작 ---");
                    _mouseProc = MouseHookCallback;      // 콜백 함수 할당
                    _mouseHookID = SetHook(_mouseProc);  // 후크 설치
                    Console.WriteLine("마우스 클릭 시 이벤트가 여기 콘솔에 표시됩니다.");

                    Console.WriteLine("\n종료하려면 아무 키나 누르세요...");
                    Console.ReadKey();

                    // 종료 시 후킹 해제
                    if (_mouseHookID != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_mouseHookID);
                    }
                }
            }
            catch (Exception ex)
            {
                // 예외 처리
                Console.WriteLine($"오류 발생: {ex.Message}");
                Console.WriteLine(ex.StackTrace);
            }

            Console.WriteLine("프로그램을 종료합니다. 아무 키나 누르세요...");
            Console.ReadKey();
        }

        // --------------------------------------------------------------------
        // [5] 마우스 후킹 관련 헬퍼 함수
        // --------------------------------------------------------------------

        /// <summary>
        /// SetHook : 마우스 훅 설치 함수 (WH_MOUSE_LL)
        /// </summary>
        /// <param name="proc">콜백 델리게이트</param>
        /// <returns>마우스 훅 핸들</returns>
        private static IntPtr SetHook(LowLevelMouseProc proc)
        {
            // 현재 프로세스/모듈 정보 가져오기
            using (Process curProcess = Process.GetCurrentProcess())
            using (ProcessModule curModule = curProcess.MainModule)
            {
                // WH_MOUSE_LL = 14
                // GetModuleHandle(curModule.ModuleName) : 모듈 핸들
                return SetWindowsHookEx(14, proc, GetModuleHandle(curModule.ModuleName), 0);
            }
        }

        /// <summary>
        /// 마우스 콜백 : 마우스 이벤트가 발생할 때마다 호출
        /// </summary>
        /// <param name="nCode">훅 코드 (0 이상이면 유효)</param>
        /// <param name="wParam">마우스 이벤트 종류(WM_LBUTTONDOWN 등)</param>
        /// <param name="lParam">추가 정보(마우스 좌표 등)</param>
        /// <returns></returns>
        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                switch ((int)wParam)
                {
                    case 0x0201: // WM_LBUTTONDOWN (왼쪽 버튼 눌림)
                        Console.WriteLine("왼쪽 마우스 버튼 클릭 감지!");
                        break;
                    case 0x0204: // WM_RBUTTONDOWN (오른쪽 버튼 눌림)
                        Console.WriteLine("오른쪽 마우스 버튼 클릭 감지!");
                        break;
                }
            }
            // 다음 훅으로 이벤트 넘겨 원래 동작 유지
            return CallNextHookEx(_mouseHookID, nCode, wParam, lParam);
        }
    }
}
