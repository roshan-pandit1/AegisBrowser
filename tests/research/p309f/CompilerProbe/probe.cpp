// Test-only C++/CLI assembly. No CEF, host configuration or network activity.
public ref class CompilerProbe
{
public:
    static int Answer() { return 42; }
};
