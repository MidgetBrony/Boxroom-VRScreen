#include <windows.h>
#include <roapi.h>
#include <d3d11.h>
#include <windows.graphics.capture.interop.h>
#include <windows.graphics.directx.direct3d11.interop.h>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Graphics.Capture.h>
#include <winrt/Windows.Graphics.DirectX.h>
#include <winrt/Windows.Graphics.DirectX.Direct3D11.h>
#include <mutex>
#include "Unity/IUnityInterface.h"
#include "Unity/IUnityGraphics.h"
#include "Unity/IUnityGraphicsD3D11.h"

#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "windowsapp.lib")

namespace wf = winrt::Windows::Foundation;
namespace wg = winrt::Windows::Graphics;
namespace wgc = winrt::Windows::Graphics::Capture;
namespace wgd = winrt::Windows::Graphics::DirectX;
namespace wgd11 = winrt::Windows::Graphics::DirectX::Direct3D11;

static IUnityInterfaces* g_unity = nullptr;
static IUnityGraphics* g_graphics = nullptr;
static ID3D11Device* g_device = nullptr;
static ID3D11DeviceContext* g_context = nullptr;
static ID3D11Texture2D* g_texture = nullptr;
static ID3D11ShaderResourceView* g_view = nullptr;
static wgd11::IDirect3DDevice g_winrtDevice{ nullptr };
static wgc::GraphicsCaptureItem g_item{ nullptr };
static wgc::Direct3D11CaptureFramePool g_pool{ nullptr };
static wgc::GraphicsCaptureSession g_session{ nullptr };
static wgc::Direct3D11CaptureFrame g_latestFrame{ nullptr };
static winrt::event_token g_frameToken{};
static HWND g_requestedWindow = nullptr;
static HWND g_activeWindow = nullptr;
static int g_width = 0, g_height = 0, g_status = 0;
static std::mutex g_mutex;

static void SetStatus(HRESULT hr) { g_status = FAILED(hr) ? static_cast<int>(hr) : 1; }

static void ReleaseCaptureResources()
{
    if (g_pool && g_frameToken.value) { try { g_pool.FrameArrived(g_frameToken); } catch (...) {} g_frameToken = {}; }
    if (g_session) { try { g_session.Close(); } catch (...) {} g_session = nullptr; }
    if (g_pool) { try { g_pool.Close(); } catch (...) {} g_pool = nullptr; }
    g_latestFrame = nullptr;
    g_item = nullptr;
    if (g_view) { g_view->Release(); g_view = nullptr; }
    if (g_texture) { g_texture->Release(); g_texture = nullptr; }
    g_activeWindow = nullptr;
    g_width = g_height = 0;
}

static bool SetupCapture(HWND hwnd)
{
    ReleaseCaptureResources();
    if (!g_device || !IsWindow(hwnd)) { g_status = -2; return false; }
    try
    {
        if (!g_winrtDevice)
        {
            IDXGIDevice* dxgiDevice = nullptr;
            HRESULT hr = g_device->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(&dxgiDevice));
            if (FAILED(hr)) { SetStatus(hr); return false; }
            IInspectable* inspectable = nullptr;
            hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, &inspectable);
            dxgiDevice->Release();
            if (FAILED(hr)) { SetStatus(hr); return false; }
            winrt::copy_from_abi(g_winrtDevice, inspectable);
            inspectable->Release();
        }

        auto interop = winrt::get_activation_factory<wgc::GraphicsCaptureItem, IGraphicsCaptureItemInterop>();
        winrt::check_hresult(interop->CreateForWindow(hwnd, winrt::guid_of<wgc::GraphicsCaptureItem>(), winrt::put_abi(g_item)));
        wg::SizeInt32 size = g_item.Size();
        if (size.Width < 16 || size.Height < 16) { g_status = -3; return false; }
        g_width = size.Width; g_height = size.Height;

        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = static_cast<UINT>(g_width); desc.Height = static_cast<UINT>(g_height);
        desc.MipLevels = 1; desc.ArraySize = 1; desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        desc.SampleDesc.Count = 1; desc.Usage = D3D11_USAGE_DEFAULT; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        winrt::check_hresult(g_device->CreateTexture2D(&desc, nullptr, &g_texture));
        winrt::check_hresult(g_device->CreateShaderResourceView(g_texture, nullptr, &g_view));

        g_pool = wgc::Direct3D11CaptureFramePool::CreateFreeThreaded(g_winrtDevice, wgd::DirectXPixelFormat::B8G8R8A8UIntNormalized, 2, size);
        g_session = g_pool.CreateCaptureSession(g_item);
        try { g_session.IsCursorCaptureEnabled(false); } catch (...) { }
        g_frameToken = g_pool.FrameArrived([](wgc::Direct3D11CaptureFramePool const& sender, wf::IInspectable const&)
        {
            try
            {
                auto frame = sender.TryGetNextFrame();
                std::lock_guard<std::mutex> lock(g_mutex);
                g_latestFrame = frame;
            }
            catch (...) { g_status = -20; }
        });
        g_session.StartCapture();
        g_activeWindow = hwnd;
        g_status = 1;
        return true;
    }
    catch (winrt::hresult_error const& error) { SetStatus(error.code()); return false; }
    catch (...) { g_status = -99; return false; }
}

static void CaptureFrame()
{
    thread_local bool apartmentInitialized = []()
    {
        HRESULT hr = RoInitialize(RO_INIT_MULTITHREADED);
        return SUCCEEDED(hr) || hr == S_FALSE || hr == RPC_E_CHANGED_MODE;
    }();
    if (!apartmentInitialized) { g_status = -21; return; }
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_requestedWindow != g_activeWindow)
    {
        HWND requested = g_requestedWindow;
        if (!requested) { ReleaseCaptureResources(); g_status = 0; return; }
        if (!SetupCapture(requested)) return;
    }
    if (!g_latestFrame || !g_texture || !g_context) return;
    try
    {
        auto access = g_latestFrame.Surface().as<::Windows::Graphics::DirectX::Direct3D11::IDirect3DDxgiInterfaceAccess>();
        ID3D11Texture2D* source = nullptr;
        winrt::check_hresult(access->GetInterface(__uuidof(ID3D11Texture2D), reinterpret_cast<void**>(&source)));
        g_context->CopyResource(g_texture, source);
        g_context->Flush();
        source->Release();
        g_latestFrame = nullptr;
        g_status = 2;
    }
    catch (winrt::hresult_error const& error) { SetStatus(error.code()); }
    catch (...) { g_status = -98; }
}

static void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
{
    if (eventType == kUnityGfxDeviceEventInitialize)
    {
        auto d3d = g_unity->Get<IUnityGraphicsD3D11>();
        if (d3d) { g_device = d3d->GetDevice(); if (g_device) g_device->GetImmediateContext(&g_context); }
    }
    else if (eventType == kUnityGfxDeviceEventShutdown)
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        ReleaseCaptureResources(); g_winrtDevice = nullptr;
        if (g_context) { g_context->Release(); g_context = nullptr; }
        g_device = nullptr;
    }
}

static void UNITY_INTERFACE_API OnRenderEvent(int) { CaptureFrame(); }

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    RoInitialize(RO_INIT_MULTITHREADED);
    g_unity = interfaces; g_graphics = interfaces->Get<IUnityGraphics>();
    g_graphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
}
extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_graphics) g_graphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    OnGraphicsDeviceEvent(kUnityGfxDeviceEventShutdown); g_graphics = nullptr; g_unity = nullptr; RoUninitialize();
}
extern "C" __declspec(dllexport) void VRScreen_StartCapture(void* hwnd) { std::lock_guard<std::mutex> lock(g_mutex); g_requestedWindow = static_cast<HWND>(hwnd); }
extern "C" __declspec(dllexport) void VRScreen_StopCapture() { std::lock_guard<std::mutex> lock(g_mutex); g_requestedWindow = nullptr; }
extern "C" __declspec(dllexport) void* VRScreen_GetTexture() { return g_view; }
extern "C" __declspec(dllexport) int VRScreen_GetWidth() { return g_width; }
extern "C" __declspec(dllexport) int VRScreen_GetHeight() { return g_height; }
extern "C" __declspec(dllexport) int VRScreen_GetStatus() { return g_status; }
extern "C" __declspec(dllexport) UnityRenderingEvent VRScreen_GetRenderEventFunc() { return OnRenderEvent; }
