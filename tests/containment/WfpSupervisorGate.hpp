#pragma once
#include <stdexcept>

// Ordering guard only. It does not verify OS state, WFP enforcement or child
// termination; callers must establish those facts using retained OS handles.
namespace AegisTestSupervisor {
enum class Phase { Created, ChildVerified, PolicyReadBack, ObserverArmed, Resumed, Exited, Cleaned, Failed };
class Gate {
    Phase phase_ = Phase::Created;
    void Advance(Phase required, Phase next) {
        if (phase_ != required) { phase_ = Phase::Failed; throw std::logic_error("Invalid supervised test transition."); }
        phase_ = next;
    }
public:
    Phase State() const { return phase_; }
    void ChildVerified() { Advance(Phase::Created, Phase::ChildVerified); }
    void PolicyReadBack() { Advance(Phase::ChildVerified, Phase::PolicyReadBack); }
    void ObserverArmed() { Advance(Phase::PolicyReadBack, Phase::ObserverArmed); }
    void Resumed() { Advance(Phase::ObserverArmed, Phase::Resumed); }
    void Exited() { Advance(Phase::Resumed, Phase::Exited); }
    void Cleaned() { Advance(Phase::Exited, Phase::Cleaned); }
};
}
