
using EmployeeApi.Data;
using EmployeeApi.Models;
using EmployeeApi.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace EmployeeApi.Controllers;

[ApiController]
[Route("api/[controller]")]
public class EmployeesController : ControllerBase
{
    private readonly AppDbContext _context;
    private readonly TelemetryService _telemetry;
    private readonly ILogger<EmployeesController> _logger;

    public EmployeesController(
        AppDbContext context,
        TelemetryService telemetry,
        ILogger<EmployeesController> logger)
    {
        _context = context;
        _telemetry = telemetry;
        _logger = logger;
    }

    // READ: Get all employees
    [HttpGet]
    public async Task<ActionResult<IEnumerable<Employee>>> GetAll()
    {
        try
        {
            var employees = await _context.Employees
                .AsNoTracking()
                .OrderBy(e => e.Id)
                .ToListAsync();

            return Ok(employees);
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(ex, "GetAllEmployees");

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = "Unable to retrieve employees." });
        }
    }

    // READ: Get employee by ID
    [HttpGet("{id:int}")]
    public async Task<ActionResult<Employee>> GetById(int id)
    {
        try
        {
            var employee = await _context.Employees
                .AsNoTracking()
                .FirstOrDefaultAsync(e => e.Id == id);

            if (employee is null)
            {
                return NotFound(
                    new { message = "Employee not found." });
            }

            return Ok(employee);
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(
                ex, "GetEmployeeById", id);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = "Unable to retrieve the employee." });
        }
    }

    // CREATE: Add employee
    [HttpPost]
    public async Task<ActionResult<Employee>> Create(Employee employee)
    {
        try
        {
            employee.Id = 0;

            _context.Employees.Add(employee);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent(
                "EmployeeCreated", "CreateEmployee", employee.Id);

            return CreatedAtAction(
                nameof(GetById),
                new { id = employee.Id },
                employee);
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(ex, "CreateEmployee");

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = "Unable to create the employee." });
        }
    }

    // UPDATE: Modify employee
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(
        int id,
        Employee request)
    {
        if (id != request.Id)
        {
            return BadRequest(
                new { message = "ID mismatch." });
        }

        try
        {
            var employee = await _context.Employees.FindAsync(id);

            if (employee is null)
            {
                return NotFound(
                    new { message = "Employee not found." });
            }

            employee.Name = request.Name;
            employee.Email = request.Email;
            employee.Department = request.Department;
            employee.Salary = request.Salary;

            await _context.SaveChangesAsync();

            _telemetry.TrackEvent(
                "EmployeeUpdated", "UpdateEmployee", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(
                ex, "UpdateEmployee", id);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = "Unable to update the employee." });
        }
    }

    // DELETE: Remove employee
    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        try
        {
            var employee = await _context.Employees.FindAsync(id);

            if (employee is null)
            {
                return NotFound(
                    new { message = "Employee not found." });
            }

            _context.Employees.Remove(employee);
            await _context.SaveChangesAsync();

            _telemetry.TrackEvent(
                "EmployeeDeleted", "DeleteEmployee", id);

            return NoContent();
        }
        catch (Exception ex)
        {
            _telemetry.TrackException(
                ex, "DeleteEmployee", id);

            return StatusCode(
                StatusCodes.Status500InternalServerError,
                new { message = "Unable to delete the employee." });
        }
    }
}
